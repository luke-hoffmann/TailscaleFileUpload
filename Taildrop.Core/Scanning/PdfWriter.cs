using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using OpenCvSharp;

namespace Taildrop.Core.Scanning;

public enum PdfImageKind
{
    /// <summary>8-bit RGB, JPEG (DCTDecode).</summary>
    ColorJpeg,
    /// <summary>8-bit gray, JPEG (DCTDecode).</summary>
    GrayJpeg,
    /// <summary>1 bit per pixel, deflated (FlateDecode): crisp black-and-white text at a fraction of the size.</summary>
    BlackWhite
}

/// <summary>One finished page of a PDF: its image stream and the paper size it fills edge to edge.</summary>
public sealed record PdfPage(byte[] Data, PdfImageKind Kind, int PixelWidth, int PixelHeight, double WidthPt, double HeightPt);

/// <summary>
/// Writes scanned pages as a PDF the way a document scanner does: each page is one full-bleed image on a real
/// paper size, at most 300 dpi. Pages whose proportions match US Letter, A4 or US Legal get exactly that size,
/// so they print 1:1; anything else (receipts, cards) keeps its own proportions, fitted inside Letter.
/// </summary>
public static class PdfWriter
{
    const double MaxDpi = 300;
    const int JpegQuality = 85;
    const double PaperTolerance = 0.03;

    static readonly (double Short, double Long)[] PaperSizes =
    {
        (612, 792),         // US Letter, 8.5 × 11 in
        (595.28, 841.89),   // A4, 210 × 297 mm
        (612, 1008)         // US Legal, 8.5 × 14 in
    };

    /// <summary>Lays out a finished scan (as rendered by <see cref="ScanPipeline"/>) as a PDF page.</summary>
    public static PdfPage CreatePage(Mat page, ScanFilter filter)
    {
        var (widthPt, heightPt) = PaperSize(page.Width, page.Height);

        // Never more than 300 dpi on the chosen paper; never upscaled.
        var scale = Math.Min(1.0, Math.Min(widthPt / 72 * MaxDpi / page.Width, heightPt / 72 * MaxDpi / page.Height));
        using var sized = new Mat();
        if (scale < 1)
            Cv2.Resize(page, sized, new Size(Math.Max(1, (int)Math.Round(page.Width * scale)), Math.Max(1, (int)Math.Round(page.Height * scale))), 0, 0, InterpolationFlags.Area);
        else
            page.CopyTo(sized);

        if (filter == ScanFilter.BlackWhite)
        {
            using var gray = sized.Channels() == 1 ? sized.Clone() : sized.CvtColor(ColorConversionCodes.BGR2GRAY);
            return new PdfPage(PackBits(gray), PdfImageKind.BlackWhite, gray.Width, gray.Height, widthPt, heightPt);
        }

        var kind = sized.Channels() == 1 ? PdfImageKind.GrayJpeg : PdfImageKind.ColorJpeg;
        return new PdfPage(ScanPipeline.Encode(sized, JpegQuality), kind, sized.Width, sized.Height, widthPt, heightPt);
    }

    /// <summary>The paper size, in points (1/72 in), for a page of the given pixel size.</summary>
    public static (double Width, double Height) PaperSize(int width, int height)
    {
        var landscape = width > height;
        double shortSide = Math.Min(width, height), longSide = Math.Max(width, height);
        var aspect = shortSide / longSide;

        foreach (var paper in PaperSizes)
        {
            if (Math.Abs(aspect / (paper.Short / paper.Long) - 1) <= PaperTolerance)
                return landscape ? (paper.Long, paper.Short) : (paper.Short, paper.Long);
        }

        // Fit inside Letter (turned to match), keeping the page's own proportions.
        var (letterShort, letterLong) = PaperSizes[0];
        var fit = Math.Min(letterShort / shortSide, letterLong / longSide);
        var (s, l) = (Math.Round(shortSide * fit, 2), Math.Round(longSide * fit, 2));
        return landscape ? (l, s) : (s, l);
    }

    /// <summary>Thresholds an 8-bit gray page to 1 bit per pixel (1 = white, rows padded to whole bytes) and deflates it.</summary>
    static byte[] PackBits(Mat gray)
    {
        var width = gray.Width;
        var stride = (width + 7) / 8;
        var line = new byte[width];
        var packed = new byte[stride];

        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            for (var r = 0; r < gray.Height; r++)
            {
                Marshal.Copy(gray.Ptr(r), line, 0, width);
                Array.Clear(packed);
                for (var c = 0; c < width; c++)
                {
                    if (line[c] >= 128) packed[c >> 3] |= (byte)(0x80 >> (c & 7));
                }
                zlib.Write(packed);
            }
        }
        return output.ToArray();
    }

    /// <summary>Writes the pages as a complete PDF file.</summary>
    public static byte[] Write(IReadOnlyList<PdfPage> pages, string title, DateTimeOffset created)
    {
        if (pages.Count == 0) throw new ArgumentException("A PDF needs at least one page", nameof(pages));

        // Objects: 1 catalog, 2 page tree, 3 document info, then per page: page, content stream, image.
        const int firstPage = 4;
        var objectCount = firstPage - 1 + pages.Count * 3;
        var offsets = new long[objectCount + 1];
        using var pdf = new MemoryStream();

        void Text(string text) => pdf.Write(Encoding.ASCII.GetBytes(text));
        void BeginObject(int number)
        {
            offsets[number] = pdf.Position;
            Text($"{number} 0 obj\n");
        }
        void Stream(string dictionary, byte[] data)
        {
            Text($"<< {dictionary} /Length {data.Length} >>\nstream\n");
            pdf.Write(data);
            Text("\nendstream\nendobj\n");
        }

        Text("%PDF-1.4\n");
        pdf.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' }); // marks the file as binary for transfer tools

        BeginObject(1);
        Text("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        BeginObject(2);
        var kids = string.Join(" ", Enumerable.Range(0, pages.Count).Select(i => $"{firstPage + i * 3} 0 R"));
        Text($"<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>\nendobj\n");

        BeginObject(3);
        var date = PdfDate(created);
        Text($"<< /Title {PdfText(title)} /Creator (Taildrop) /Producer (Taildrop) /CreationDate ({date}) /ModDate ({date}) >>\nendobj\n");

        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            var (pageObject, contentObject, imageObject) = (firstPage + i * 3, firstPage + i * 3 + 1, firstPage + i * 3 + 2);
            var width = Number(page.WidthPt);
            var height = Number(page.HeightPt);

            BeginObject(pageObject);
            Text($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] " +
                 $"/Resources << /XObject << /Im1 {imageObject} 0 R >> /ProcSet [/PDF /ImageB /ImageC] >> /Contents {contentObject} 0 R >>\nendobj\n");

            BeginObject(contentObject);
            Stream("", Encoding.ASCII.GetBytes($"q {width} 0 0 {height} 0 0 cm /Im1 Do Q"));

            BeginObject(imageObject);
            var format = page.Kind switch
            {
                PdfImageKind.ColorJpeg => "/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode",
                PdfImageKind.GrayJpeg => "/ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /DCTDecode",
                _ => "/ColorSpace /DeviceGray /BitsPerComponent 1 /Filter /FlateDecode"
            };
            Stream($"/Type /XObject /Subtype /Image /Width {page.PixelWidth} /Height {page.PixelHeight} {format}", page.Data);
        }

        var xref = pdf.Position;
        Text($"xref\n0 {objectCount + 1}\n0000000000 65535 f \n");
        for (var n = 1; n <= objectCount; n++) Text($"{offsets[n]:D10} 00000 n \n");
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        Text($"trailer\n<< /Size {objectCount + 1} /Root 1 0 R /Info 3 0 R /ID [<{id}> <{id}>] >>\nstartxref\n{xref}\n%%EOF\n");
        return pdf.ToArray();
    }

    static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>A PDF text string that holds any characters: UTF-16BE with a byte-order mark, in hex.</summary>
    static string PdfText(string text) => "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text)) + ">";

    static string PdfDate(DateTimeOffset time)
    {
        var offset = time.Offset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        offset = offset.Duration();
        return string.Create(CultureInfo.InvariantCulture, $"D:{time:yyyyMMddHHmmss}{sign}{offset.Hours:D2}'{offset.Minutes:D2}'");
    }
}
