using System.Diagnostics;
using OpenCvSharp;
using Taildrop.Core.Scanning;
using Xunit;
using Xunit.Abstractions;

namespace Taildrop.Core.Tests;

/// <summary>
/// Building a page from many frames, on a synthetic A4 document at 300 dpi filmed like a phone would (see
/// <see cref="SyntheticDocument"/>): one frame of the whole page, then close-ups.
/// </summary>
public sealed class DetailCanvasTests : IDisposable
{
    readonly ITestOutputHelper _output;
    readonly Mat _document;
    static readonly Size Portrait = new(1080, 1920);

    public DetailCanvasTests(ITestOutputHelper output)
    {
        _output = output;
        _document = SyntheticDocument.Make(2480, 3508, seed: 11);
    }

    public void Dispose() => _document.Dispose();

    [Fact]
    public void CloseUpsFillInDetailTheFirstFrameCouldNotResolve()
    {
        var (reference, outline) = SyntheticDocument.WholePage(_document, Portrait);
        using var _ = reference;
        using var canvas = DetailCanvas.Start(reference, outline);
        using var before = canvas.Snapshot();
        var startProgress = canvas.Progress;
        _output.WriteLine($"canvas {canvas.Width}x{canvas.Height}, grid {canvas.Columns}x{canvas.Rows}, start {startProgress:P0}");
        Assert.InRange(canvas.Width, 2400, 2560);
        Assert.InRange(canvas.Height, 3400, 3620);
        Assert.False(canvas.Complete);

        var accepted = 0;
        var clock = Stopwatch.StartNew();
        var closeUps = SyntheticDocument.CloseUps(_document, Portrait, columns: 4, rows: 5, density: 1.2, seed: 5).ToList();
        foreach (var frame in closeUps)
        {
            using (frame)
            {
                var step = canvas.Add(frame);
                if (step.Accepted) accepted++;
                else _output.WriteLine($"  frame not used: {step.Problem}");
            }
        }
        _output.WriteLine($"{accepted}/{closeUps.Count} frames used, {clock.ElapsedMilliseconds / closeUps.Count} ms each, progress {canvas.Progress:P0}");

        var levels = canvas.Levels();
        for (var row = 0; row < canvas.Rows; row++)
            _output.WriteLine("  " + string.Join(" ", levels.Skip(row * canvas.Columns).Take(canvas.Columns).Select(l => l.ToString().PadLeft(3))));

        using var after = canvas.Snapshot();
        using var truth = new Mat();
        Cv2.Resize(_document, truth, after.Size(), 0, 0, InterpolationFlags.Area);
        var detailBefore = DetailCorrelation(before, truth);
        var detailAfter = DetailCorrelation(after, truth);
        var (medianShift, worstShift) = Misalignment(after, truth);
        var shading = ShadingDifference(after, before);
        _output.WriteLine($"fine detail vs truth: {detailBefore:F2} -> {detailAfter:F2}; misalignment median {medianShift:F2} px, worst {worstShift:F2} px; shading diff {shading:F1}");
        if (Environment.GetEnvironmentVariable("TAILDROP_DEBUG_DIR") is { Length: > 0 } debug)
        {
            Directory.CreateDirectory(debug);
            Cv2.ImWrite(Path.Combine(debug, "detail-before.png"), before);
            Cv2.ImWrite(Path.Combine(debug, "detail-after.png"), after);
            Cv2.ImWrite(Path.Combine(debug, "detail-truth.png"), truth);
        }

        Assert.True(accepted >= closeUps.Count - 1, $"only {accepted} of {closeUps.Count} close-ups were placed");
        Assert.True(canvas.Complete, $"progress {canvas.Progress:P0}");

        Assert.True(detailAfter > 0.88, $"fine print not recovered ({detailAfter:F2})");
        Assert.True(detailAfter > detailBefore + 0.12, $"close-ups added too little ({detailBefore:F2} -> {detailAfter:F2})");
        Assert.True(medianShift < 0.5, $"close-ups misplaced by {medianShift:F2} px");
        Assert.True(worstShift < 1.5, $"a close-up misplaced by {worstShift:F2} px");
        Assert.True(shading < 12, $"exposure differences between frames show as patches ({shading:F1})");
    }

    [Fact]
    public void BlurryAndForeignFramesAreLeftOut()
    {
        var (reference, outline) = SyntheticDocument.WholePage(_document, Portrait);
        using var _ = reference;
        using var canvas = DetailCanvas.Start(reference, outline);

        var frames = SyntheticDocument.CloseUps(_document, Portrait, columns: 2, rows: 2, density: 1.2, seed: 9).ToList();
        using (var sharp = frames[0]) Assert.True(canvas.Add(sharp).Accepted);

        // Camera shake, slight to severe, either way: a little is still usable, a lot is left out.
        foreach (var (length, vertical, usable) in new[] { (5, false, true), (9, false, false), (13, true, false), (23, false, false) })
        using (var shaken = frames[1].Clone())
        {
            using var kernel = vertical ? new Mat(length, 1, MatType.CV_32FC1, Scalar.All(1.0 / length)) : new Mat(1, length, MatType.CV_32FC1, Scalar.All(1.0 / length));
            Cv2.Filter2D(shaken, shaken, -1, kernel);
            var step = canvas.Add(shaken);
            _output.WriteLine($"shake {length} px{(vertical ? " vertical" : "")}: {(step.Accepted ? "used" : step.Problem)}");
            if (usable) Assert.True(step.Accepted);
            else Assert.False(step.Accepted);
            if (!usable) Assert.Contains(step.Problem, new[] { "blurry", "lost" });
        }

        // Another document altogether.
        using (var other = SyntheticDocument.Make(2480, 3508, seed: 99))
        {
            var foreign = SyntheticDocument.CloseUps(other, Portrait, columns: 1, rows: 1, density: 1.2, seed: 2).Single();
            using (foreign)
            {
                var step = canvas.Add(foreign);
                Assert.False(step.Accepted);
                Assert.Equal("lost", step.Problem);
            }
        }
        foreach (var frame in frames.Skip(1)) frame.Dispose();
    }

    [Fact]
    public void APageAlreadySharpEnoughIsCompleteRightAway()
    {
        // Straight on and close: the first frame already has a camera pixel for every page pixel needed.
        using var small = new Mat();
        Cv2.Resize(_document, small, new Size(1240, 1754), 0, 0, InterpolationFlags.Area);
        var frameSize = new Size(1500, 2000);
        var corners = new[] { new Point2d(130, 123), new Point2d(1370, 123), new Point2d(1370, 1877), new Point2d(130, 1877) };
        using var frame = SyntheticDocument.Render(small, corners, frameSize, gain: 1, seed: 1);
        var outline = ScanOutline.FromCorners(corners.Select(c => new[] { c.X / frameSize.Width, c.Y / frameSize.Height }).ToArray(), true);
        using var canvas = DetailCanvas.Start(frame, outline, targetLongEdge: 1754);
        Assert.True(canvas.Complete, $"progress {canvas.Progress:P0}");
    }

    [Fact]
    public void FingerprintKnowsThePageAcrossLightingAndRotation()
    {
        var (reference, outline) = SyntheticDocument.WholePage(_document, Portrait);
        using var _ = reference;
        var first = PageFingerprint.FromFrame(reference, outline.Corners);
        Assert.NotNull(first);

        // The same page, darker and turned a quarter turn in the frame.
        var (turned, turnedOutline) = SyntheticDocument.WholePage(_document, new Size(1920, 1080), gain: 0.75, quarterTurn: true);
        using (turned) Assert.True(PageFingerprint.Same(first, PageFingerprint.FromFrame(turned, turnedOutline.Corners)));

        using var flat = new Mat();
        Cv2.Resize(_document, flat, new Size(620, 877), 0, 0, InterpolationFlags.Area);
        Assert.True(PageFingerprint.Same(first, PageFingerprint.FromPage(flat)));

        using var other = SyntheticDocument.Make(2480, 3508, seed: 4);
        var (next, nextOutline) = SyntheticDocument.WholePage(other, Portrait);
        using (next) Assert.False(PageFingerprint.Same(first, PageFingerprint.FromFrame(next, nextOutline.Corners)));

        using var blank = new Mat(877, 620, MatType.CV_8UC3, new Scalar(236, 240, 242));
        Assert.Null(PageFingerprint.FromPage(blank));
    }

    [Fact]
    public void QuickDetectionFindsThePageInALiveFrame()
    {
        using var scene = new SyntheticScene(new SceneOptions { Width = 1080, Height = 1920, Fill = 0.55, TiltDeg = 12, Backdrop = Backdrop.DarkWood });
        var found = DocumentDetector.DetectQuick(scene.Photo);
        Assert.NotNull(found);
        Assert.True(found!.Value.Confident);
        for (var i = 0; i < 4; i++)
        {
            Assert.InRange(found.Value.Corners[i][0], scene.TruthOutline.Corners[i][0] - 0.02, scene.TruthOutline.Corners[i][0] + 0.02);
            Assert.InRange(found.Value.Corners[i][1], scene.TruthOutline.Corners[i][1] - 0.02, scene.TruthOutline.Corners[i][1] + 0.02);
        }

        using var empty = new Mat(1920, 1080, MatType.CV_8UC3, new Scalar(40, 60, 90));
        Cv2.Randn(empty, new Scalar(40, 60, 90), new Scalar(6, 6, 6));
        Assert.Null(DocumentDetector.DetectQuick(empty));
    }

    // ---- measures -----------------------------------------------------------------------------------------------

    /// <summary>Correlation of fine detail (band-pass, about 2-5 px periods: finer than the first frame resolves) between an image and the truth.</summary>
    static double DetailCorrelation(Mat image, Mat truth)
    {
        static Mat Band(Mat m)
        {
            using var gray = m.CvtColor(ColorConversionCodes.BGR2GRAY);
            using var g32 = new Mat();
            gray.ConvertTo(g32, MatType.CV_32F);
            using var fine = new Mat();
            using var coarse = new Mat();
            Cv2.GaussianBlur(g32, fine, new Size(0, 0), 0.6);
            Cv2.GaussianBlur(g32, coarse, new Size(0, 0), 1.5);
            var band = new Mat();
            Cv2.Subtract(fine, coarse, band);
            return band;
        }
        using var a = Band(image);
        using var b = Band(truth);
        using var product = new Mat();
        Cv2.Multiply(a, b, product);
        var ab = Cv2.Sum(product).Val0;
        Cv2.Multiply(a, a, product);
        var aa = Cv2.Sum(product).Val0;
        Cv2.Multiply(b, b, product);
        var bb = Cv2.Sum(product).Val0;
        return ab / Math.Sqrt(aa * bb);
    }

    /// <summary>Shift between image and truth in textured tiles (phase correlation): median and worst, in pixels.</summary>
    static (double Median, double Worst) Misalignment(Mat image, Mat truth)
    {
        using var a = image.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var b = truth.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var a32 = new Mat();
        using var b32 = new Mat();
        a.ConvertTo(a32, MatType.CV_32F);
        b.ConvertTo(b32, MatType.CV_32F);
        const int Tile = 256;
        var shifts = new List<double>();
        using var window = new Mat();
        Cv2.CreateHanningWindow(window, new Size(Tile, Tile), MatType.CV_32F);
        for (var y = 300; y + Tile < image.Height - 300; y += Tile)
            for (var x = 250; x + Tile < image.Width - 250; x += Tile)
            {
                using var ta = new Mat(a32, new Rect(x, y, Tile, Tile));
                using var tb = new Mat(b32, new Rect(x, y, Tile, Tile));
                Cv2.MeanStdDev(tb, out _, out var spread);
                if (spread.Val0 < 25) continue;
                var shift = Cv2.PhaseCorrelate(ta, tb, window, out var response);
                if (response < 0.1) continue;
                shifts.Add(Math.Sqrt(shift.X * shift.X + shift.Y * shift.Y));
            }
        shifts.Sort();
        return (shifts[shifts.Count / 2], shifts[^1]);
    }

    /// <summary>Largest difference in large-scale brightness between two renderings of the page (gray levels).</summary>
    static double ShadingDifference(Mat a, Mat b)
    {
        static Mat Shade(Mat m)
        {
            using var gray = m.CvtColor(ColorConversionCodes.BGR2GRAY);
            using var small = new Mat();
            Cv2.Resize(gray, small, new Size(m.Width / 16, m.Height / 16), 0, 0, InterpolationFlags.Area);
            var blurred = new Mat();
            Cv2.GaussianBlur(small, blurred, new Size(0, 0), 3);
            return blurred;
        }
        using var sa = Shade(a);
        using var sb = Shade(b);
        using var difference = new Mat();
        Cv2.Absdiff(sa, sb, difference);
        // Ignore the outermost ring: the first frame's flattening shows a hint of table at the very edge.
        using var inner = new Mat(difference, new Rect(4, 4, difference.Width - 8, difference.Height - 8));
        Cv2.MinMaxLoc(inner, out double _, out double max);
        return max;
    }
}
