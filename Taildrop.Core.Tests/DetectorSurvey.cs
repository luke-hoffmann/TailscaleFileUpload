using OpenCvSharp;
using System.Diagnostics;
using Taildrop.Core.Scanning;
using Xunit;
using Xunit.Abstractions;

namespace Taildrop.Core.Tests;

public class DetectorSurvey
{
    readonly ITestOutputHelper _output;
    public DetectorSurvey(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Survey()
    {
        var scenes = new (string, SceneOptions)[]
        {
            ("dark-wood tilted", new SceneOptions()),
            ("blue-cloth", new SceneOptions { Backdrop = Backdrop.BlueCloth, TiltDeg = 10, YawDeg = -8, RollDeg = 20 }),
            ("tan-wood", new SceneOptions { Backdrop = Backdrop.TanWood, RollDeg = -12 }),
            ("granite", new SceneOptions { Backdrop = Backdrop.Granite, TiltDeg = 30, YawDeg = 15 }),
            ("light-desk", new SceneOptions { Backdrop = Backdrop.LightDesk, TiltDeg = 14, YawDeg = -6, RollDeg = 9 }),
            ("light-desk-bent", new SceneOptions { Backdrop = Backdrop.LightDesk, PageMmWidth = 80, PageMmHeight = 200, Fill = 0.7, PillowXPx = 14, RollDeg = -8 }),
            ("white-desk", new SceneOptions { Backdrop = Backdrop.WhiteDesk }),
            ("snake", new SceneOptions { SnakePx = 18 }),
            ("pillow", new SceneOptions { PillowXPx = 24, PillowYPx = 16 }),
            ("receipt-curled", new SceneOptions { PageMmWidth = 80, PageMmHeight = 240, TiltDeg = 25, YawDeg = 8, RollDeg = -14, Fill = 0.8, PillowXPx = 20 }),
            ("receipt-granite", new SceneOptions { PageMmWidth = 80, PageMmHeight = 240, Backdrop = Backdrop.Granite, TiltDeg = 10, YawDeg = 0, RollDeg = 20, Fill = 0.55, SnakePx = 10 }),
            ("portrait", new SceneOptions { Width = 1200, Height = 1600, RollDeg = 0, TiltDeg = 15, YawDeg = 10, Fill = 0.7 }),
            ("steep", new SceneOptions { TiltDeg = 38, YawDeg = -20, RollDeg = -10 }),
            ("big-12MP", new SceneOptions { Width = 4032, Height = 3024, TiltDeg = 18, YawDeg = 9, RollDeg = 4, PillowXPx = 40, Backdrop = Backdrop.TanWood }),
        };
        foreach (var (name, options) in scenes)
        {
            using var scene = new SyntheticScene(options);
            var clock = Stopwatch.StartNew();
            var outline = DocumentDetector.Detect(scene.Photo);
            var detectMs = clock.ElapsedMilliseconds;
            using var mask = PageAnalysis.OutlineMask(outline, options.Width, options.Height);
            var iou = PageAnalysis.Iou(mask, scene.TruthMask);
            clock.Restart();
            using var flat = PageFlattener.Flatten(scene.Photo, outline);
            var flatMs = clock.ElapsedMilliseconds;
            var markers = PageAnalysis.FindMarkers(flat).Count;
            var bars = PageAnalysis.MeasureBars(flat, SyntheticScene.BarFractions);
            var worstOffset = bars.Max(b => Math.Abs(b.Offset));
            var worstSpread = bars.Max(b => b.Spread);
            var cornerError = outline.Corners.Zip(scene.TruthOutline.Corners, (d, t) =>
                Math.Sqrt(Math.Pow((d[0] - t[0]) * options.Width, 2) + Math.Pow((d[1] - t[1]) * options.Height, 2))).Max();
            _output.WriteLine($"{name,-18} IoU {iou:F3} conf {outline.Confident,-5} detect {detectMs,4} ms flat {flatMs,3} ms out {flat.Width}x{flat.Height} markers {markers} barOffset {worstOffset:P2} barSpread {worstSpread:P2} cornerErr {cornerError:F1}px");
            if (iou < 0.9) _output.WriteLine(DocumentDetector.Describe(scene.Photo));
        }
    }
}
