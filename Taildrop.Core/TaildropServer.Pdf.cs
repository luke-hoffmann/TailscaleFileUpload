using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Taildrop.Core.Scanning;

namespace Taildrop.Core;

/// <summary>
/// Multi-page PDF scans. Each page is an ordinary scan (same detection, review, filters, rotation and edge editing),
/// but instead of becoming its own JPEG it becomes a page of one PDF in the inbox, which is rewritten after every
/// change so it is always complete on the PC.
///
///   POST   /api/scan   + X-Scan-Pdf: new | {pdf id}   [+ X-Scan-Pdf-Index: n]   -> 201 scan with "pdf"
///   PUT    /api/scan/{id}                              re-renders the page, rewrites the PDF
///   DELETE /api/scan/{id}                              takes the page out of the PDF (the file goes with the last page)
///
/// Every scan description of a PDF page carries <c>pdf: { id, name, page, pages, size }</c>.
/// </summary>
public sealed partial class TaildropServer
{
    const int MaxPdfPages = 100;
    const int MaxPdfSessions = 5;

    sealed record PdfPageEntry(string ScanId, PdfPage Page);

    sealed class PdfSession
    {
        public required string Id { get; init; }
        /// <summary>"Scan 2026-10-02 at 14.31.05": the file name (plus ".pdf") and the document title.</summary>
        public required string Stem { get; init; }
        public required DateTimeOffset Created { get; init; }
        public DateTime LastUsed { get; set; }
        public string FileName { get; set; } = "";
        public long Size { get; set; }
        /// <summary>Replaced as a whole (under <see cref="Gate"/>), so readers always see one consistent list.</summary>
        public PdfPageEntry[] Pages { get; set; } = Array.Empty<PdfPageEntry>();
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    readonly ConcurrentDictionary<string, PdfSession> _pdfs = new(StringComparer.Ordinal);

    async Task CreatePdfPageAsync(HttpContext context, ScanFilter filter, string target)
    {
        var ct = context.RequestAborted;
        PdfSession? pdf = null;
        if (target != "new" && (!ScanIdPattern.IsMatch(target) || !_pdfs.TryGetValue(target, out pdf)))
            throw new UploadException(404, "That PDF is no longer available. Tap Done and start a new one.");
        if (pdf is not null && pdf.Pages.Length >= MaxPdfPages)
            throw new UploadException(400, $"A PDF can have up to {MaxPdfPages} pages. Tap Done and start a new one.");

        int? insertAt = null;
        var position = context.Request.Headers["X-Scan-Pdf-Index"].ToString();
        if (position.Length > 0)
        {
            if (!int.TryParse(position, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                throw new UploadException(400, "That page position isn't valid");
            insertAt = index;
        }

        var data = await ReadBodyAsync(context.Request, MaxScanPhotoSize, "That photo is larger than 64 MB", ct);
        using var photo = ScanPipeline.Decode(data);
        var outline = await Task.Run(() => ScanPipeline.Detect(photo), ct);
        var render = await ScanPipeline.RenderPdfPageAsync(photo, outline, filter, 0, ct);
        var source = ScanPipeline.EncodeSource(photo);

        if (pdf is null)
        {
            var now = DateTimeOffset.Now;
            pdf = new PdfSession { Id = Guid.NewGuid().ToString("N"), Stem = ScanFileStem(now.DateTime), Created = now };
        }

        var id = Guid.NewGuid().ToString("N");
        var session = new ScanSession
        {
            Id = id,
            Directory = Path.Combine(_inboxDir, ".scans", id),
            Created = DateTime.UtcNow,
            Outline = outline,
            Filter = filter,
            Pdf = pdf
        };

        try
        {
            Directory.CreateDirectory(session.Directory);
            await File.WriteAllBytesAsync(session.OriginalPath, data, ct);
            await File.WriteAllBytesAsync(session.SourcePath, source, ct);
            await File.WriteAllBytesAsync(session.PreviewPath, render.Preview, ct);

            await pdf.Gate.WaitAsync(ct);
            try
            {
                if (target != "new" && !_pdfs.ContainsKey(pdf.Id))
                    throw new UploadException(404, "That PDF is no longer available. Tap Done and start a new one.");
                if (pdf.Pages.Length >= MaxPdfPages)
                    throw new UploadException(400, $"A PDF can have up to {MaxPdfPages} pages. Tap Done and start a new one.");

                var pages = pdf.Pages.ToList();
                pages.Insert(Math.Clamp(insertAt ?? pages.Count, 0, pages.Count), new PdfPageEntry(id, render.Page));
                await SavePdfAsync(pdf, pages.ToArray(), ct);
                pdf.LastUsed = DateTime.UtcNow;
                _pdfs[pdf.Id] = pdf;
            }
            finally
            {
                pdf.Gate.Release();
            }
        }
        catch
        {
            try { Directory.Delete(session.Directory, recursive: true); } catch { /* best effort */ }
            throw;
        }

        ApplyRender(session, render.Page);
        _scans[id] = session;
        EvictOldScans();
        EvictOldPdfs();
        await JsonAsync(context.Response, 201, DescribeScan(session));
    }

    /// <summary>Puts a re-rendered page in place of the old one and rewrites the PDF.</summary>
    async Task ReplacePdfPageAsync(PdfSession pdf, string scanId, PdfPage page, CancellationToken ct)
    {
        await pdf.Gate.WaitAsync(ct);
        try
        {
            var pages = pdf.Pages.ToArray();
            var index = Array.FindIndex(pages, p => p.ScanId == scanId);
            if (index < 0) throw new UploadException(404, "That page is no longer in the PDF");
            pages[index] = new PdfPageEntry(scanId, page);
            await SavePdfAsync(pdf, pages, ct);
            pdf.LastUsed = DateTime.UtcNow;
        }
        finally
        {
            pdf.Gate.Release();
        }
    }

    /// <summary>DELETE of a PDF page: the page leaves the PDF (and the PDF leaves the inbox with its last page).</summary>
    async Task RemovePdfPageAsync(HttpContext context, ScanSession session, PdfSession pdf)
    {
        var ct = context.RequestAborted;
        await session.Gate.WaitAsync(ct); // not while the page is being re-rendered
        try
        {
            await pdf.Gate.WaitAsync(ct);
            try
            {
                var pages = pdf.Pages.Where(p => p.ScanId != session.Id).ToArray();
                if (pages.Length != pdf.Pages.Length) await SavePdfAsync(pdf, pages, ct);
            }
            finally
            {
                pdf.Gate.Release();
            }
            DiscardScan(session);
        }
        finally
        {
            session.Gate.Release();
        }
        await JsonAsync(context.Response, 200, new { ok = true, pdf = DescribePdf(pdf) });
    }

    /// <summary>
    /// Writes the PDF made of <paramref name="pages"/> to the inbox (or removes it when there are none left), then
    /// makes them the PDF's pages. The caller holds the PDF's gate; on failure nothing changes.
    /// </summary>
    async Task SavePdfAsync(PdfSession pdf, PdfPageEntry[] pages, CancellationToken ct)
    {
        if (pages.Length == 0)
        {
            if (pdf.FileName.Length > 0)
            {
                try { File.Delete(SafeInboxPath(pdf.FileName)); }
                catch (IOException) { throw new UploadException(409, "That file is open on the computer. Close it and try again."); }
            }
            pdf.FileName = "";
            pdf.Size = 0;
            pdf.Pages = pages;
            return;
        }

        var title = pdf.FileName.Length > 0 ? Path.GetFileNameWithoutExtension(pdf.FileName) : pdf.Stem;
        var bytes = PdfWriter.Write(pages.Select(p => p.Page).ToList(), title, pdf.Created);
        pdf.FileName = await ReplaceInboxFileAsync(pdf.FileName, pdf.Stem + ".pdf", bytes, ct);
        pdf.Size = bytes.Length;
        pdf.Pages = pages;
    }

    static object DescribePdf(PdfSession pdf, string? scanId = null)
    {
        var pages = pdf.Pages;
        return new
        {
            id = pdf.Id,
            name = pdf.FileName,
            page = scanId is null ? 0 : Array.FindIndex(pages, p => p.ScanId == scanId) + 1,
            pages = pages.Length,
            size = pdf.Size
        };
    }

    static void ApplyRender(ScanSession session, PdfPage page)
    {
        session.Width = page.PixelWidth;
        session.Height = page.PixelHeight;
        session.Size = page.Data.Length;
    }

    /// <summary>Forgets a PDF and the scans of its pages. Its file stays in the inbox; it just can't be edited any more.</summary>
    void DiscardPdf(PdfSession pdf)
    {
        _pdfs.TryRemove(pdf.Id, out _);
        foreach (var session in _scans.Values.Where(s => s.Pdf == pdf).ToList())
            DiscardScan(session);
    }

    /// <summary>Keeps memory bounded: only the most recently used PDFs can take more pages.</summary>
    void EvictOldPdfs()
    {
        while (_pdfs.Count > MaxPdfSessions)
            DiscardPdf(_pdfs.Values.OrderBy(p => p.LastUsed).First());
    }
}
