using OpenCvSharp;
using Taildrop.Core.Scanning;
using Xunit;
using Xunit.Abstractions;

namespace Taildrop.Core.Tests;

public class FlattenerTests
{
    readonly ITestOutputHelper _output;
    public FlattenerTests(ITestOutputHelper output) => _output = output;

    public static IEnumerable<object[]> Scenarios() => new[]
    {
        new object[] { "front-on", new SceneOptions { TiltDeg = 0, YawDeg = 0, RollDeg = 0 } },
        new object[] { "tilted", new SceneOptions() },
        new object[] { "steep-tilt", new SceneOptions { TiltDeg = 38, YawDeg = -20, RollDeg = -10 } },
        new object[] { "snake-bent", new SceneOptions { SnakePx = 18 } },
        new object[] { "pillow-bent", new SceneOptions { PillowXPx = 24, PillowYPx = 16 } },
        new object[] { "receipt-curled", new SceneOptions { PageMmWidth = 80, PageMmHeight = 240, TiltDeg = 25, YawDeg = 8, RollDeg = -14, Fill = 0.8, PillowXPx = 20 } },
        new object[] { "portrait-photo", new SceneOptions { Width = 1200, Height = 1600, RollDeg = 90, TiltDeg = 15, YawDeg = 10, Fill = 0.5 } },
    };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void FlattensWholePageStraightAndComplete(string name, SceneOptions options)
    {
        using var scene = new SyntheticScene(options);
        using var flat = PageFlattener.Flatten(scene.Photo, scene.TruthOutline);
        _output.WriteLine($"{name}: output {flat.Width}x{flat.Height}, true aspect {scene.TrueAspect:F3}, got {(double)flat.Width / flat.Height:F3}");

        // Proportions of the paper are recovered, not just the photographed (foreshortened) shape.
        Assert.InRange((double)flat.Width / flat.Height / scene.TrueAspect, 0.97, 1.03);

        // All four corner markers present, each in its own corner: nothing at the edges was cut off.
        var markers = PageAnalysis.FindMarkers(flat, (scene.PageTexelsWide, scene.PageTexelsHigh));
        Assert.Equal(4, markers.Count);
        var dx = SyntheticScene.MarkerCenterInset / (double)scene.PageTexelsWide;
        var dy = SyntheticScene.MarkerCenterInset / (double)scene.PageTexelsHigh;
        foreach (var expected in new[] { new Point2d(dx, dy), new Point2d(1 - dx, dy), new Point2d(1 - dx, 1 - dy), new Point2d(dx, 1 - dy) })
        {
            var nearest = markers.Min(m => Math.Max(Math.Abs(m.X - expected.X), Math.Abs(m.Y - expected.Y)));
            Assert.True(nearest < 0.015, $"marker near ({expected.X:F3},{expected.Y:F3}) missing; nearest off by {nearest:F4}");
        }

        // Reference bars are straight, horizontal and where the printed page puts them.
        var bars = PageAnalysis.FindBars(flat);
        _output.WriteLine("bars: " + string.Join(", ", bars.Select(b => b.ToString("F3"))));
        Assert.Equal(SyntheticScene.BarFractions.Length, bars.Count);
        for (var i = 0; i < bars.Count; i++) Assert.InRange(Math.Abs(bars[i] - SyntheticScene.BarFractions[i]), 0, 0.012);
    }

    [Fact]
    public void AspectRatioEstimateMatchesPhysicalPageUnderPerspective()
    {
        // (tilt, yaw, roll, focal factor). Yaw 0 leaves one edge pair parallel, where the focal length is not
        // recoverable and a typical-phone prior is used, so those cases use a camera that is off the prior.
        foreach (var (tilt, yaw, roll, focal) in new[]
                 { (30.0, 15.0, 5.0, 0.75), (40.0, -25.0, 20.0, 0.6), (22.0, 0.0, -30.0, 0.75), (28.0, 0.0, 10.0, 0.62), (28.0, 0.0, 10.0, 0.95) })
        {
            var options = new SceneOptions { TiltDeg = tilt, YawDeg = yaw, RollDeg = roll, FocalFactor = focal, PageMmWidth = 100, PageMmHeight = 233 };
            using var scene = new SyntheticScene(options);
            var corners = scene.TruthOutline.Corners.Select(c => new Point2d(c[0] * options.Width, c[1] * options.Height)).ToArray();
            var chord = (Geometry.Distance(corners[0], corners[1]) + Geometry.Distance(corners[3], corners[2]))
                        / (Geometry.Distance(corners[0], corners[3]) + Geometry.Distance(corners[1], corners[2]));
            var estimate = PageFlattener.EstimateAspectRatio(corners, options.Width, options.Height, chord);
            _output.WriteLine($"tilt {tilt}: true {scene.TrueAspect:F3} chord {chord:F3} estimate {estimate:F3}");
            // Exact when the focal length is recoverable; within ~6% (vs ~13% for raw edge lengths) on the typical-phone prior.
            var tolerance = yaw == 0 ? 0.06 : 0.03;
            Assert.InRange(estimate / scene.TrueAspect, 1 - tolerance, 1 + tolerance);
        }
    }
}
