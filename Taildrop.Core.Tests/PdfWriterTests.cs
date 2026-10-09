using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using OpenCvSharp;
using Taildrop.Core.Scanning;
using Xunit;

namespace Taildrop.Core.Tests;

/// <summary>Just enough PDF reading to check what <see cref="PdfWriter"/> wrote.</summary>
public sealed class PdfInspector
{
    public string Text { get; }
    public byte[] Bytes { get; }

    public PdfInspector(byte[] bytes)
    {
        Bytes = bytes;
        Text = Encoding.Latin1.GetString(bytes);
    }

    public int PageCount => int.Parse(Regex.Match(Text, @"/Type /Pages /Kids \[[^\]]*\] /Count (\d+)").Groups[1].Value, CultureInfo.InvariantCulture);

    public List<(double Width, double Height)> MediaBoxes => Regex.Matches(Text, @"/MediaBox \[0 0 ([\d.]+) ([\d.]+)\]")
        .Select(m => (double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)))
        .ToList();

    /// <summary>Per image: its pixel size, color space, bits and filter.</summary>
    public List<(int Width, int Height, string ColorSpace, int Bits, string Filter)> Images => Regex.Matches(Text,
            @"/Subtype /Image /Width (\d+) /Height (\d+) /ColorSpace /(\w+) /BitsPerComponent (\d+) /Filter /(\w+)")
        .Select(m => (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Value, int.Parse(m.Groups[4].Value), m.Groups[5].Value))
        .ToList();

    /// <summary>Every cross-reference entry points at the start of its object, and startxref at the table.</summary>
    public void AssertWellFormed()
    {
        Assert.StartsWith("%PDF-1.4\n", Text);
        Assert.EndsWith("%%EOF\n", Text);
        var startxref = long.Parse(Regex.Match(Text, @"startxref\n(\d+)\n%%EOF\n$").Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.StartsWith("xref\n", Text[(int)startxref..]);
        var header = Regex.Match(Text[(int)startxref..], @"^xref\n0 (\d+)\n");
        var count = int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture);
        var entries = Text[((int)startxref + header.Length)..].Split('\n').Take(count).ToList();
        Assert.Equal("0000000000 65535 f ", entries[0]);
        for (var n = 1; n < count; n++)
        {
            Assert.Equal(19, entries[n].Length); // + "\n" = the fixed 20 bytes
            var offset = int.Parse(entries[n][..10], CultureInfo.InvariantCulture);
            Assert.StartsWith($"{n} 0 obj\n", Text[offset..]);
        }
        Assert.Contains($"/Size {count} /Root 1 0 R", Text);

        // Stream lengths match their data.
        foreach (Match stream in Regex.Matches(Text, @"/Length (\d+) >>\nstream\n"))
        {
            var length = int.Parse(stream.Groups[1].Value, CultureInfo.InvariantCulture);
            Assert.StartsWith("\nendstream\nendobj\n", Text[(stream.Index + stream.Length + length)..]);
        }
    }
}

public class PdfWriterTests
{
    [Theory]
    [InlineData(2550, 3300, 612, 792)]         // Letter
    [InlineData(3300, 2550, 792, 612)]         // Letter, landscape
    [InlineData(2480, 3508, 595.28, 841.89)]   // A4
    [InlineData(2430, 3508, 595.28, 841.89)]   // A4, proportions measured 2% off
    [InlineData(2550, 4200, 612, 1008)]        // Legal
    [InlineData(800, 3200, 198, 792)]          // receipt: own proportions, as tall as Letter
    [InlineData(3000, 3000, 612, 612)]         // square: as wide as Letter
    public void PaperSizeSnapsToStandardSheetsAndFitsTheRest(int width, int height, double widthPt, double heightPt)
    {
        var (w, h) = PdfWriter.PaperSize(width, height);
        Assert.Equal(widthPt, w, 2);
        Assert.Equal(heightPt, h, 2);
    }

    [Fact]
    public void WritesAValidMultiPagePdf_ColorGrayAndBlackWhite()
    {
        using var color = new Mat(3508, 2480, MatType.CV_8UC3, Scalar.All(255));   // A4 at 300 dpi
        Cv2.PutText(color, "Invoice", new Point(200, 400), HersheyFonts.HersheySimplex, 8, new Scalar(160, 40, 20), 12);
        using var gray = new Mat(4400, 3400, MatType.CV_8UC1, Scalar.All(250));     // Letter at 400 dpi: gets downsampled
        Cv2.PutText(gray, "Page two", new Point(200, 400), HersheyFonts.HersheySimplex, 8, Scalar.All(30), 12);
        using var bw = new Mat(1100, 850, MatType.CV_8UC1, Scalar.All(255));        // Letter at 100 dpi: never upscaled
        Cv2.Rectangle(bw, new Rect(100, 100, 300, 50), Scalar.All(0), -1);

        var pages = new[]
        {
            PdfWriter.CreatePage(color, ScanFilter.Auto),
            PdfWriter.CreatePage(gray, ScanFilter.Gray),
            PdfWriter.CreatePage(bw, ScanFilter.BlackWhite)
        };
        var created = new DateTimeOffset(2026, 10, 2, 14, 31, 5, TimeSpan.FromHours(-7));
        var pdf = new PdfInspector(PdfWriter.Write(pages, "Scan 2026-10-02 at 14.31.05 — Müller", created));

        pdf.AssertWellFormed();
        Assert.Equal(3, pdf.PageCount);
        Assert.Equal(new[] { (595.28, 841.89), (612.0, 792.0), (612.0, 792.0) }, pdf.MediaBoxes);
        Assert.Equal(new[]
        {
            (2480, 3508, "DeviceRGB", 8, "DCTDecode"),
            (2550, 3300, "DeviceGray", 8, "DCTDecode"),
            (850, 1100, "DeviceGray", 1, "FlateDecode")
        }, pdf.Images);
        Assert.Contains("/CreationDate (D:20261002143105-07'00')", pdf.Text);
        Assert.Contains("/Title <FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes("Scan 2026-10-02 at 14.31.05 — Müller")) + ">", pdf.Text);

        // The JPEG pages decode, and the 1-bit page holds exactly the black bar.
        using (var decoded = Cv2.ImDecode(pages[1].Data, ImreadModes.Unchanged))
            Assert.Equal((2550, 3300, 1), (decoded.Width, decoded.Height, decoded.Channels()));
        using var inflated = new MemoryStream();
        using (var zlib = new ZLibStream(new MemoryStream(pages[2].Data), CompressionMode.Decompress)) zlib.CopyTo(inflated);
        var bits = inflated.ToArray();
        var stride = (850 + 7) / 8;
        Assert.Equal(stride * 1100, bits.Length);
        bool White(int x, int y) => (bits[y * stride + x / 8] & (0x80 >> (x % 8))) != 0;
        Assert.False(White(150, 120));
        Assert.True(White(50, 50));
        Assert.True(White(500, 120));
        Assert.Equal(1100 * 850 - 300 * 50, Enumerable.Range(0, 1100).Sum(y => Enumerable.Range(0, 850).Count(x => White(x, y))));
    }
}
