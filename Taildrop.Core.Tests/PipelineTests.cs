using OpenCvSharp;
using System.Diagnostics;
using Taildrop.Core.Scanning;
using Xunit;
using Xunit.Abstractions;

namespace Taildrop.Core.Tests;

public class PipelineTests
{
    readonly ITestOutputHelper _output;
    public PipelineTests(ITestOutputHelper output) => _output = output;

    static byte[] ToJpeg(Mat photo, int quality = 90)
    {
        Cv2.ImEncode(".jpg", photo, out var bytes, new ImageEncodingParam(ImwriteFlags.JpegQuality, quality));
        return bytes;
    }

    /// <summary>Inserts an EXIF APP1 segment carrying only the Orientation tag, right after the JPEG start marker.</summary>
    static byte[] WithExifOrientation(byte[] jpeg, int orientation)
    {
        byte[] exif =
        {
            0xFF, 0xE1, 0x00, 0x22,
            (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            (byte)'M', (byte)'M', 0x00, 0x2A, 0, 0, 0, 8,
            0x00, 0x01,
            0x01, 0x12, 0x00, 0x03, 0, 0, 0, 1, 0, (byte)orientation, 0, 0,
            0, 0, 0, 0
        };
        return jpeg.Take(2).Concat(exif).Concat(jpeg.Skip(2)).ToArray();
    }

    [Fact]
    public async Task PhotoToScan_EndToEnd_KeepsWholePageAtFullResolution()
    {
        using var scene = new SyntheticScene(new SceneOptions { Width = 4032, Height = 3024, TiltDeg = 18, YawDeg = 9, RollDeg = 4, PillowXPx = 40, Backdrop = Backdrop.TanWood });
        var jpeg = ToJpeg(scene.Photo);
        var clock = Stopwatch.StartNew();

        using var photo = ScanPipeline.Decode(jpeg);
        var outline = ScanPipeline.Detect(photo);
        var render = await ScanPipeline.RenderAsync(photo, outline, ScanFilter.Auto, 0);
        _output.WriteLine($"12 MP photo -> {render.Width}x{render.Height}, {render.Jpeg.Length / 1024} KB, {clock.ElapsedMilliseconds} ms total (outline confident: {outline.Confident})");

        Assert.True(outline.Confident);
        Assert.True(clock.ElapsedMilliseconds < 5000, "a scan should take a few seconds at most");
        // The page spans roughly 2400 photo pixels on its long side: the scan keeps that resolution (no downscaling).
        Assert.True(Math.Max(render.Width, render.Height) > 2200);

        using var result = Cv2.ImDecode(render.Jpeg, ImreadModes.Color);
        Assert.Equal(render.Width, result.Width);
        Assert.Equal(4, PageAnalysis.FindMarkers(result, (scene.PageTexelsWide, scene.PageTexelsHigh)).Count);
        Assert.InRange((double)render.Width / render.Height / scene.TrueAspect, 0.97, 1.03);
        Assert.True(render.Preview.Length < render.Jpeg.Length);
    }

    [Fact]
    public void Decode_AppliesExifOrientation()
    {
        using var scene = new SyntheticScene(new SceneOptions());
        // Store the pixels turned 90° counter-clockwise and tell decoders (EXIF orientation 6) to turn them back.
        using var stored = new Mat();
        Cv2.Rotate(scene.Photo, stored, RotateFlags.Rotate90Counterclockwise);
        var jpeg = WithExifOrientation(ToJpeg(stored), 6);

        using var decoded = ScanPipeline.Decode(jpeg);
        Assert.Equal(scene.Photo.Width, decoded.Width);
        Assert.Equal(scene.Photo.Height, decoded.Height);

        var outline = ScanPipeline.Detect(decoded);
        using var mask = PageAnalysis.OutlineMask(outline, decoded.Width, decoded.Height);
        Assert.True(PageAnalysis.Iou(mask, scene.TruthMask) > 0.95);
    }

    [Fact]
    public void Filters_ProduceExpectedKindsOfImage()
    {
        using var scene = new SyntheticScene(new SceneOptions());
        using var photo = ScanPipeline.Decode(ToJpeg(scene.Photo));
        var outline = ScanPipeline.Detect(photo);
        using var flat = PageFlattener.Flatten(photo, outline);

        using var auto = ScanEnhancer.Apply(flat, ScanFilter.Auto);
        using var gray = ScanEnhancer.Apply(flat, ScanFilter.Gray);
        using var bw = ScanEnhancer.Apply(flat, ScanFilter.BlackWhite);
        using var original = ScanEnhancer.Apply(flat, ScanFilter.Original);

        Assert.Equal(3, auto.Channels());
        Assert.Equal(1, gray.Channels());
        Assert.Equal(1, bw.Channels());
        using (var black = new Mat())
        using (var white = new Mat())
        {
            Cv2.Compare(bw, Scalar.All(0), black, CmpTypes.EQ);
            Cv2.Compare(bw, Scalar.All(255), white, CmpTypes.EQ);
            Assert.Equal(bw.Rows * bw.Cols, Cv2.CountNonZero(black) + Cv2.CountNonZero(white)); // pure black and white only
        }
        Assert.Equal(0, Cv2.Norm(flat, original, NormTypes.L1));

        // Lighting varies by about +-10% across the frame; after clean-up the blank paper should be uniformly near-white,
        // far more even than in the original.
        // Blank paper = the left/right margins (no print there). Measure how even and how white it is.
        (double Mean, double Std) Margin(Mat image)
        {
            using var g = image.Channels() == 1 ? image.Clone() : image.CvtColor(ColorConversionCodes.BGR2GRAY);
            var h = g.Height;
            var w = g.Width;
            using var left = new Mat(g, new Rect((int)(w * 0.015), (int)(h * 0.1), (int)(w * 0.025), (int)(h * 0.8)));
            using var right = new Mat(g, new Rect((int)(w * 0.96), (int)(h * 0.1), (int)(w * 0.025), (int)(h * 0.8)));
            using var both = new Mat();
            Cv2.HConcat(new[] { left, right }, both);
            Cv2.MeanStdDev(both, out var mean, out var std);
            _output.WriteLine($"margin brightness mean {mean.Val0:F1} std {std.Val0:F1}");
            return (mean.Val0, std.Val0);
        }
        var before = Margin(original);
        var after = Margin(auto);
        Assert.True(after.Std < before.Std * 0.5, $"lighting should be evened out ({after.Std:F1} vs {before.Std:F1})");
        Assert.True(after.Mean > 240, "paper should come out white");
        Assert.True(before.Mean < 235, "(sanity) the unprocessed photo is visibly grayer");
    }

    [Fact]
    public void Render_UsesFullChromaAndRotates()
    {
        using var scene = new SyntheticScene(new SceneOptions());
        var outline = scene.TruthOutline;
        var upright = ScanPipeline.Render(scene.Photo, outline, ScanFilter.Auto, 0);
        var turned = ScanPipeline.Render(scene.Photo, outline, ScanFilter.Auto, 90);
        Assert.Equal(upright.Width, turned.Height);
        Assert.Equal(upright.Height, turned.Width);

        // JPEG start-of-frame: every component sampled 1x1 means 4:4:4 (no chroma subsampling).
        var jpeg = upright.Jpeg;
        var i = 2;
        while (i + 9 < jpeg.Length)
        {
            if (jpeg[i] == 0xFF && jpeg[i + 1] is 0xC0 or 0xC2)
            {
                var components = jpeg[i + 9];
                Assert.Equal(3, components);
                for (var c = 0; c < components; c++) Assert.Equal(0x11, jpeg[i + 11 + c * 3]);
                return;
            }
            i += 2 + ((jpeg[i + 2] << 8) | jpeg[i + 3]);
        }
        Assert.Fail("no JPEG frame header found");
    }

    [Fact]
    public void Decode_RejectsUnusableInput()
    {
        var heic = new byte[64];
        System.Text.Encoding.ASCII.GetBytes("ftypheic").CopyTo(heic, 4);
        Assert.Equal(415, Assert.Throws<ScanException>(() => ScanPipeline.Decode(heic)).StatusCode);
        Assert.Equal(415, Assert.Throws<ScanException>(() => ScanPipeline.Decode(Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray())).StatusCode);
        Assert.Equal(400, Assert.Throws<ScanException>(() => ScanPipeline.Decode(new byte[3])).StatusCode);

        // A tiny JPEG header that claims to be 40000 x 40000 pixels is refused before any pixel memory is allocated.
        byte[] huge = { 0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x9C, 0x40, 0x9C, 0x40, 0x03, 1, 0x11, 0, 2, 0x11, 1, 3, 0x11, 1, 0xFF, 0xD9 };
        Assert.Equal(413, Assert.Throws<ScanException>(() => ScanPipeline.Decode(huge)).StatusCode);
    }

    [Fact]
    public void LowContrastPage_IsStillFound()
    {
        // White paper on a white desk: the page edge is faint, but it is the page that gets scanned.
        using var scene = new SyntheticScene(new SceneOptions { Backdrop = Backdrop.WhiteDesk });
        var outline = ScanPipeline.Detect(scene.Photo);
        using var mask = PageAnalysis.OutlineMask(outline, scene.Options.Width, scene.Options.Height);
        Assert.True(PageAnalysis.Iou(mask, scene.TruthMask) > 0.95);
        var render = ScanPipeline.Render(scene.Photo, outline, ScanFilter.Auto, 0);
        Assert.True(render.Jpeg.Length > 1000);
    }

    [Fact]
    public void NoPage_FallsBackToManualAdjustment()
    {
        // Nothing but a textured table: the phone opens the corner editor instead of guessing.
        using var scene = new SyntheticScene(new SceneOptions { Backdrop = Backdrop.Granite });
        using var table = new Mat();
        Cv2.Resize(new Mat(scene.Photo, new Rect(0, 0, scene.Photo.Width / 6, scene.Photo.Height / 6)), table, scene.Photo.Size(), 0, 0, InterpolationFlags.Cubic);
        var outline = ScanPipeline.Detect(table);
        Assert.False(outline.Confident);
        var render = ScanPipeline.Render(table, outline, ScanFilter.Auto, 0);
        Assert.True(render.Jpeg.Length > 1000);
    }

    [Fact]
    public void Sanitize_RepairsOrRejectsOutlinesFromTheNetwork()
    {
        var good = ScanOutline.FromCorners(new[] { new[] { .1, .1 }, new[] { .9, .1 }, new[] { .9, .9 }, new[] { .1, .9 } }, true);
        var cleaned = ScanOutline.Sanitize(good);
        Assert.NotNull(cleaned);
        Assert.Equal(ScanOutline.EdgeSamples, cleaned!.Top.Length);

        // Wrong number of edge samples, edge ends not on the corners: resampled and pinned.
        var sloppy = new ScanOutline
        {
            Corners = good.Corners,
            Top = new[] { new[] { .2, .12 }, new[] { .5, .05 }, new[] { .8, .12 } },
            Right = good.Right, Bottom = good.Bottom, Left = good.Left
        };
        var repaired = ScanOutline.Sanitize(sloppy)!;
        Assert.Equal(ScanOutline.EdgeSamples, repaired.Top.Length);
        Assert.Equal(good.Corners[0], repaired.Top[0]);
        Assert.Equal(good.Corners[1], repaired.Top[^1]);
        Assert.True(repaired.Top[16][1] < 0.1); // the bow survives

        Assert.Null(ScanOutline.Sanitize(null));
        Assert.Null(ScanOutline.Sanitize(new ScanOutline { Corners = new[] { new[] { double.NaN, 0.0 }, new[] { 1.0, 0.0 }, new[] { 1.0, 1.0 }, new[] { 0.0, 1.0 } } }));
        Assert.Null(ScanOutline.Sanitize(ScanOutline.FromCorners(new[] { new[] { .5, .5 }, new[] { .5, .5 }, new[] { .5, .5 }, new[] { .5, .5 } }, true)));
    }
}
