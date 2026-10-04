using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using OpenCvSharp;
using Taildrop.Core.Scanning;

namespace Taildrop.Core;

/// <summary>
/// Document scanning endpoints. The phone uploads a photo; the PC finds the page, flattens it and drops the
/// finished JPEG into the inbox. The original photo stays in a hidden per-scan folder so the phone can
/// re-render the same file with a corrected outline, another filter or a rotation, or retake it.
/// Pages built live from many camera frames (see TaildropServer.Live.cs) keep their flat page instead: they can
/// change look and rotation, but have no single photo whose edges could be adjusted.
///
///   POST   /api/scan                 raw photo body (X-Scan-Filter optional)  -> 201 scan
///   PUT    /api/scan/{id}            {outline?, filter?, rotate?}             -> 200 scan (file replaced)
///   DELETE /api/scan/{id}            removes the scan and its inbox file      -> 200
///   GET    /api/scan/{id}/preview    ~900 px JPEG of the result
///   GET    /api/scan/{id}/source     ~1600 px JPEG of the photo (EXIF applied) the outline's coordinates refer to
///
/// State-changing calls require an <c>X-Taildrop</c> header, which no cross-site form or simple request can send.
/// </summary>
public sealed partial class TaildropServer
{
    const long MaxScanPhotoSize = 64L * 1024 * 1024;
    const long MaxScanUpdateSize = 64L * 1024;
    const int MaxScanSessions = 30;
    static readonly Regex ScanIdPattern = new("^[a-f0-9]{32}$", RegexOptions.Compiled);

    sealed class ScanSession
    {
        public required string Id { get; init; }
        public required string Directory { get; init; }
        public required DateTime Created { get; init; }
        public string FileName { get; set; } = "";
        public ScanOutline Outline { get; set; } = new();
        public ScanFilter Filter { get; set; }
        public int Rotate { get; set; }
        /// <summary>Built live from many frames: re-rendered from the stored flat page, not from a photo and outline.</summary>
        public bool Live { get; init; }
        public int Version { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public long Size { get; set; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public string OriginalPath => Path.Combine(Directory, "original.jpg");
        public string SourcePath => Path.Combine(Directory, "source.jpg");
        public string PreviewPath => Path.Combine(Directory, "preview.jpg");
        public string FlatPath => Path.Combine(Directory, "flat.jpg");
    }

    sealed class ScanUpdate
    {
        public ScanOutline? Outline { get; set; }
        public string? Filter { get; set; }
        public int? Rotate { get; set; }
    }

    readonly ConcurrentDictionary<string, ScanSession> _scans = new(StringComparer.Ordinal);

    static readonly Lazy<bool> ScanEngineAvailable = new(() =>
    {
        try
        {
            using var probe = new OpenCvSharp.Mat(2, 2, OpenCvSharp.MatType.CV_8UC3);
            return true;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Document scanning unavailable: " + error.Message);
            return false;
        }
    });

    /// <summary>False when the native OpenCV library could not be loaded (the phone then offers plain file sending only).</summary>
    public static bool ScanAvailable => ScanEngineAvailable.Value;

    /// <summary>The desktop UI renamed an inbox file; keep the scan that produced it pointing at the new name.</summary>
    public void OnInboxFileRenamed(string oldName, string newName)
    {
        foreach (var session in _scans.Values)
            if (string.Equals(session.FileName, oldName, StringComparison.Ordinal)) session.FileName = newName;
    }

    /// <summary>The desktop UI deleted an inbox file; drop the scan session (and its stored photo) behind it.</summary>
    public void OnInboxFileRemoved(string name)
    {
        foreach (var session in _scans.Values.Where(s => string.Equals(s.FileName, name, StringComparison.Ordinal)).ToList())
            DiscardScan(session);
    }

    async Task ScanAsync(HttpContext context, string rest)
    {
        var req = context.Request;
        var res = context.Response;
        var parts = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (!ScanAvailable)
            throw new UploadException(503, "Scanning isn't available on this computer. You can still send photos and files.");

        if (req.Method is "POST" or "PUT" or "DELETE" && !req.Headers.ContainsKey("X-Taildrop"))
            throw new UploadException(400, "Missing X-Taildrop header");

        if (parts.Length == 0 && req.Method == "POST")
        {
            await CreateScanAsync(context);
            return;
        }

        if (parts.Length is 1 or 2 && ScanIdPattern.IsMatch(parts[0]))
        {
            if (!_scans.TryGetValue(parts[0], out var session))
                throw new UploadException(404, "That scan is no longer available");

            switch (parts.Length, req.Method)
            {
                case (1, "PUT"):
                    await UpdateScanAsync(context, session);
                    return;
                case (1, "DELETE"):
                    DiscardScan(session, deleteInboxFile: true);
                    await JsonAsync(res, 200, new { ok = true });
                    return;
                case (2, "GET") when parts[1] == "preview":
                    await SendImageAsync(context, session.PreviewPath);
                    return;
                case (2, "GET") when parts[1] == "source":
                    await SendImageAsync(context, session.SourcePath);
                    return;
            }
        }

        throw new UploadException(404, "Not found");
    }

    async Task CreateScanAsync(HttpContext context)
    {
        var ct = context.RequestAborted;
        var filter = ParseFilter(context.Request.Headers["X-Scan-Filter"].ToString(), ScanFilter.Auto);
        var data = await ReadBodyAsync(context.Request, MaxScanPhotoSize, "That photo is larger than 64 MB", ct);

        using var photo = ScanPipeline.Decode(data);
        var outline = await Task.Run(() => ScanPipeline.Detect(photo), ct);
        var render = await ScanPipeline.RenderAsync(photo, outline, filter, 0, ct);
        var source = ScanPipeline.EncodeSource(photo);

        var id = Guid.NewGuid().ToString("N");
        var session = new ScanSession
        {
            Id = id,
            Directory = Path.Combine(_inboxDir, ".scans", id),
            Created = DateTime.UtcNow,
            Outline = outline,
            Filter = filter
        };

        try
        {
            Directory.CreateDirectory(session.Directory);
            await File.WriteAllBytesAsync(session.OriginalPath, data, ct);
            await File.WriteAllBytesAsync(session.SourcePath, source, ct);
            await File.WriteAllBytesAsync(session.PreviewPath, render.Preview, ct);
            session.FileName = await WriteNewInboxFileAsync(render.Jpeg, ct);
        }
        catch
        {
            try { Directory.Delete(session.Directory, recursive: true); } catch { /* best effort */ }
            throw;
        }

        ApplyRender(session, render);
        _scans[id] = session;
        EvictOldScans();
        await JsonAsync(context.Response, 201, DescribeScan(session));
    }

    async Task UpdateScanAsync(HttpContext context, ScanSession session)
    {
        var ct = context.RequestAborted;
        var body = await ReadBodyAsync(context.Request, MaxScanUpdateSize, "That request is too large", ct);
        ScanUpdate? update;
        try { update = JsonSerializer.Deserialize<ScanUpdate>(body, JsonOptions); }
        catch (JsonException) { throw new UploadException(400, "That request isn't valid"); }
        if (update is null) throw new UploadException(400, "That request isn't valid");

        await session.Gate.WaitAsync(ct);
        try
        {
            if (!_scans.ContainsKey(session.Id)) throw new UploadException(404, "That scan is no longer available");

            var outline = session.Outline;
            if (update.Outline is not null && session.Live)
                throw new UploadException(400, "The edges of a live scan can't be adjusted. Retake it instead.");
            if (update.Outline is not null)
                outline = ScanOutline.Sanitize(update.Outline) ?? throw new UploadException(400, "That outline isn't valid");
            var filter = update.Filter is null ? session.Filter : ParseFilter(update.Filter, session.Filter, strict: true);
            var rotate = update.Rotate ?? session.Rotate;
            if (rotate is not (0 or 90 or 180 or 270)) throw new UploadException(400, "Rotation must be 0, 90, 180 or 270");

            byte[] original;
            try { original = await File.ReadAllBytesAsync(session.Live ? session.FlatPath : session.OriginalPath, ct); }
            catch (IOException) { throw new UploadException(404, "That scan is no longer available"); }

            using var photo = ScanPipeline.Decode(original);
            var render = session.Live
                ? await ScanPipeline.RenderFlatAsync(photo, filter, rotate, ct)
                : await ScanPipeline.RenderAsync(photo, outline, filter, rotate, ct);
            await File.WriteAllBytesAsync(session.PreviewPath, render.Preview, ct);
            await ReplaceInboxFileAsync(session, render.Jpeg, ct);

            session.Outline = outline;
            session.Filter = filter;
            session.Rotate = rotate;
            session.Version++;
            ApplyRender(session, render);
        }
        finally
        {
            session.Gate.Release();
        }

        await JsonAsync(context.Response, 200, DescribeScan(session));
    }

    static void ApplyRender(ScanSession session, ScanRender render)
    {
        session.Width = render.Width;
        session.Height = render.Height;
        session.Size = render.Jpeg.Length;
    }

    static object DescribeScan(ScanSession session) => new
    {
        id = session.Id,
        name = session.FileName,
        width = session.Width,
        height = session.Height,
        size = session.Size,
        confident = session.Outline.Confident,
        filter = FilterName(session.Filter),
        rotate = session.Rotate,
        version = session.Version,
        live = session.Live,
        adjustable = !session.Live,
        outline = session.Outline,
        previewUrl = $"/api/scan/{session.Id}/preview?v={session.Version}",
        sourceUrl = session.Live ? null : $"/api/scan/{session.Id}/source"
    };

    /// <summary>Saves a page built live from many frames: a new inbox file, plus a session for look/rotation changes and retakes.</summary>
    async Task<object> SaveLiveScanAsync(Mat flat, ScanOutline outline, ScanFilter filter, CancellationToken ct)
    {
        var render = await ScanPipeline.RenderFlatAsync(flat, filter, 0, ct);
        var id = Guid.NewGuid().ToString("N");
        var session = new ScanSession
        {
            Id = id,
            Directory = Path.Combine(_inboxDir, ".scans", id),
            Created = DateTime.UtcNow,
            Outline = outline,
            Filter = filter,
            Live = true
        };

        try
        {
            Directory.CreateDirectory(session.Directory);
            await File.WriteAllBytesAsync(session.FlatPath, ScanPipeline.EncodeFlat(flat), ct);
            await File.WriteAllBytesAsync(session.PreviewPath, render.Preview, ct);
            session.FileName = await WriteNewInboxFileAsync(render.Jpeg, ct);
        }
        catch
        {
            try { Directory.Delete(session.Directory, recursive: true); } catch { /* best effort */ }
            throw;
        }

        ApplyRender(session, render);
        _scans[id] = session;
        EvictOldScans();
        return DescribeScan(session);
    }

    static async Task SendImageAsync(HttpContext context, string path)
    {
        byte[] bytes;
        try { bytes = await File.ReadAllBytesAsync(path, context.RequestAborted); }
        catch (IOException) { throw new UploadException(404, "That scan is no longer available"); }

        var res = context.Response;
        res.StatusCode = 200;
        res.ContentType = "image/jpeg";
        res.ContentLength = bytes.Length;
        // Scans are private: don't let the phone's browser keep copies around.
        res.Headers.CacheControl = "no-store";
        await res.Body.WriteAsync(bytes, context.RequestAborted);
    }

    /// <summary>Writes a brand-new, uniquely named scan file into the inbox and returns its name.</summary>
    async Task<string> WriteNewInboxFileAsync(byte[] jpeg, CancellationToken ct)
    {
        var now = DateTime.Now;
        var wanted = string.Create(CultureInfo.InvariantCulture, $"Scan {now:yyyy-MM-dd} at {now:HH.mm.ss}.jpg");
        var name = await ReserveAvailableNameAsync(wanted);
        try
        {
            var finalPath = SafeInboxPath(name);
            var temp = TempPartPath();
            try
            {
                await File.WriteAllBytesAsync(temp, jpeg, ct);
                File.Move(temp, finalPath);
            }
            catch
            {
                try { File.Delete(temp); } catch { /* best effort */ }
                throw;
            }
            return name;
        }
        finally
        {
            await ReleaseNameAsync(name);
        }
    }

    /// <summary>
    /// Replaces the scan's inbox file in place. If the PC user already removed it, the scan comes back as a
    /// new file rather than being silently lost.
    /// </summary>
    async Task ReplaceInboxFileAsync(ScanSession session, byte[] jpeg, CancellationToken ct)
    {
        var existing = session.FileName.Length > 0 ? SafeInboxPath(session.FileName) : null;
        if (existing is null || !File.Exists(existing))
        {
            session.FileName = await WriteNewInboxFileAsync(jpeg, ct);
            return;
        }

        var temp = TempPartPath();
        try
        {
            await File.WriteAllBytesAsync(temp, jpeg, ct);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(temp, existing, overwrite: true);
                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    await Task.Delay(150, ct); // briefly held open by Explorer's preview, antivirus, ...
                }
                catch (IOException)
                {
                    throw new UploadException(409, "That file is open on the computer. Close it and try again.");
                }
            }
        }
        finally
        {
            try { File.Delete(temp); } catch { /* already moved, or best effort */ }
        }
    }

    string TempPartPath() => Path.Combine(_inboxDir, $".taildrop-{Guid.NewGuid():N}.part");

    void DiscardScan(ScanSession session, bool deleteInboxFile = false)
    {
        _scans.TryRemove(session.Id, out _);
        if (deleteInboxFile && session.FileName.Length > 0)
        {
            try { File.Delete(SafeInboxPath(session.FileName)); } catch { /* best effort */ }
        }
        try { Directory.Delete(session.Directory, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Keeps disk use bounded: only the most recent scans stay editable.</summary>
    void EvictOldScans()
    {
        while (_scans.Count > MaxScanSessions)
        {
            var oldest = _scans.Values.OrderBy(s => s.Created).First();
            _scans.TryRemove(oldest.Id, out _);
            try { Directory.Delete(oldest.Directory, recursive: true); } catch { /* best effort */ }
        }
    }

    static async Task<byte[]> ReadBodyAsync(HttpRequest req, long maxBytes, string tooLargeMessage, CancellationToken ct)
    {
        var length = req.ContentLength;
        if (length is > 0 && length > maxBytes) throw new UploadException(413, tooLargeMessage);

        if (length is > 0)
        {
            var exact = new byte[length.Value];
            try { await req.Body.ReadExactlyAsync(exact, ct); }
            catch (EndOfStreamException) { throw new UploadException(400, "The upload was cut short"); }
            return exact;
        }

        using var stream = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await req.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (stream.Length + read > maxBytes) throw new UploadException(413, tooLargeMessage);
            stream.Write(buffer, 0, read);
        }
        return stream.ToArray();
    }

    static ScanFilter ParseFilter(string? text, ScanFilter fallback, bool strict = false) => text?.Trim().ToLowerInvariant() switch
    {
        null or "" => fallback,
        "auto" => ScanFilter.Auto,
        "gray" or "grey" => ScanFilter.Gray,
        "bw" => ScanFilter.BlackWhite,
        "original" => ScanFilter.Original,
        _ when strict => throw new UploadException(400, "Unknown filter"),
        _ => fallback
    };

    static string FilterName(ScanFilter filter) => filter switch
    {
        ScanFilter.Gray => "gray",
        ScanFilter.BlackWhite => "bw",
        ScanFilter.Original => "original",
        _ => "auto"
    };
}
