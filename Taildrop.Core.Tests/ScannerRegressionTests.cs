using OpenCvSharp;
using Taildrop.Core.Scanning;
using Xunit;
using Xunit.Abstractions;

namespace Taildrop.Core.Tests;

/// <summary>Cases from real use: another sheet against a corner, a folded-over corner, a faint page edge.</summary>
public class ScannerRegressionTests
{
    readonly ITestOutputHelper _output;
    public ScannerRegressionTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void SheetUnderACorner_IsLeftOut(int corner)
    {
        // Like a packet whose folded-back sheet lies against the page: only the page itself is scanned.
        using var scene = new SyntheticScene(new SceneOptions { Backdrop = Backdrop.TanWood, TabCorner = corner, Fill = 0.6, RollDeg = 8 });
        var outline = ScanPipeline.Detect(scene.Photo);
        using var mask = PageAnalysis.OutlineMask(outline, scene.Options.Width, scene.Options.Height);
        var iou = PageAnalysis.Iou(mask, scene.TruthMask);
        using var foreign = new Mat();
        Cv2.Subtract(mask, scene.TruthMask, foreign);
        var foreignShare = Cv2.CountNonZero(foreign) / (double)Math.Max(1, Cv2.CountNonZero(mask));
        _output.WriteLine($"corner {corner}: IoU {iou:F3}, foreign {foreignShare:P1}, {DocumentDetector.Describe(scene.Photo)}");
        Assert.True(iou > 0.95, $"IoU {iou:F3}");
        Assert.True(foreignShare < 0.03, $"foreign {foreignShare:P1}");
    }

    [Fact]
    public void FoldedCorner_IsUnfoldedAndCleaned()
    {
        // The corner is folded over: the outline still runs to where the corner would be, and the scan shows
        // paper there, not the table.
        using var scene = new SyntheticScene(new SceneOptions { Backdrop = Backdrop.DarkWood, DogEarMm = 45 });
        var outline = ScanPipeline.Detect(scene.Photo);
        var w = scene.Options.Width;
        var h = scene.Options.Height;
        var diag = Math.Sqrt(w * (double)w + h * (double)h);
        var errors = outline.Corners.Zip(scene.TruthOutline.Corners, (a, b) => Math.Sqrt(Math.Pow((a[0] - b[0]) * w, 2) + Math.Pow((a[1] - b[1]) * h, 2))).ToArray();
        _output.WriteLine($"corner errors (px): {string.Join(", ", errors.Select(e => e.ToString("F1")))}; {DocumentDetector.Describe(scene.Photo)}");
        Assert.True(errors.Max() < 0.02 * diag, "every corner, the folded one included, is where the unfolded page's corner is");

        var render = ScanPipeline.Render(scene.Photo, outline, ScanFilter.Original, 0);
        using var page = Cv2.ImDecode(render.Jpeg, ImreadModes.Color);
        // The folded-away triangle (top-left, inside the fold line) should be paper-coloured, not dark wood.
        var size = (int)(Math.Min(page.Width, page.Height) * 0.06);
        using var cornerPatch = new Mat(page, new Rect(2, 2, size, size));
        var mean = Cv2.Mean(cornerPatch);
        var brightness = (mean.Val0 + mean.Val1 + mean.Val2) / 3;
        _output.WriteLine($"folded corner brightness {brightness:F0}");
        Assert.True(brightness > 150, $"the folded-away corner is filled with paper colour ({brightness:F0})");
    }

    [Fact]
    public void HolePunches_AreFilled()
    {
        // Three binder holes with the dark table showing through come out as paper; nothing else changes.
        using var scene = new SyntheticScene(new SceneOptions { Backdrop = Backdrop.DarkWood, HolePunches = true, Lighting = false });
        var outline = ScanPipeline.Detect(scene.Photo);
        var render = ScanPipeline.Render(scene.Photo, outline, ScanFilter.Original, 0);
        using var page = Cv2.ImDecode(render.Jpeg, ImreadModes.Color);
        foreach (var fy in new[] { 0.2, 0.5, 0.8 })
        {
            var cx = (int)(page.Width * 10 / scene.Options.PageMmWidth);
            var cy = (int)(page.Height * fy);
            var r = (int)(page.Width * 2.0 / scene.Options.PageMmWidth);
            using var hole = new Mat(page, new Rect(cx - r, cy - r, 2 * r, 2 * r));
            var mean = Cv2.Mean(hole);
            var brightness = (mean.Val0 + mean.Val1 + mean.Val2) / 3;
            _output.WriteLine($"hole at {fy:P0}: brightness {brightness:F0}");
            Assert.True(brightness > 150, $"hole at {fy:P0} is filled ({brightness:F0})");
        }
        // Detection is unaffected by the holes.
        using var mask = PageAnalysis.OutlineMask(outline, scene.Options.Width, scene.Options.Height);
        Assert.True(PageAnalysis.Iou(mask, scene.TruthMask) > 0.97);
    }
}
