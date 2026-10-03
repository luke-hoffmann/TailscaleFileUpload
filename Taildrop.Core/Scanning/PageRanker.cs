using System.Text.Json;
using OpenCvSharp;

namespace Taildrop.Core.Scanning;

/// <summary>
/// Picks the page among the scored quadrilaterals with a small tree ensemble trained on photos with known page
/// outlines (tools/ScanBench/train_ranker.py). The hand-tuned score is good at "is this a sheet edge", but
/// telling the whole sheet from a panel of a folded sheet, a block of print on it or a packet underneath it
/// takes several cues at once; the trees learned how to weigh them.
/// </summary>
static class PageRanker
{
    public static readonly string[] FeatureNames =
    {
        "total", "support", "strength", "contrast", "paper", "shape", "size", "border", "brighter", "aspect",
        "area", "score_rel", "area_rel", "contains_top", "inside_top", "corners_on", "print_inside", "extra_paper",
        "in_gray", "in_sat", "out_gray", "out_sat", "interior_bg", "outband_paper", "side_lift_max", "side_lift_min",
        "src_lines", "src_edges", "src_weak", "src_bright", "src_neutral", "rank", "n_larger_close", "n_smaller_close",
        "rules_pick",
    };

    /// <summary>Features of each candidate; <paramref name="scored"/> is sorted by hand-tuned score, best first.</summary>
    public static float[][] Features(QuadFinder.Evidence e, IReadOnlyList<QuadFinder.Quad> scored)
    {
        var top = scored[0];
        var topArea = Math.Abs(Geometry.SignedArea(top.Corners));
        var frame = e.Width * (double)e.Height;
        var areas = scored.Select(q => Math.Abs(Geometry.SignedArea(q.Corners))).ToArray();
        // The hand-written whole-sheet rules' choice is a strong hint of its own.
        var rules = QuadFinder.WholeSheet(e, scored).Corners;
        var result = new float[scored.Count][];
        for (var i = 0; i < scored.Count; i++)
        {
            var q = scored[i];
            var b = QuadFinder.Score(e, q.Corners);
            var overlap = QuadFinder.Overlap(top.Corners, q.Corners);
            var grows = i > 0 && areas[i] > 1.05 * topArea;
            var p = Photometrics(e, q.Corners);
            int largerClose = 0, smallerClose = 0;
            for (var j = 0; j < scored.Count; j++)
            {
                if (j == i || scored[j].Score < q.Score - 0.1) continue;
                if (areas[j] > 1.1 * areas[i]) largerClose++;
                else if (areas[j] < areas[i] / 1.1) smallerClose++;
            }
            var src = q.Source.Split('+')[0];
            result[i] = new[]
            {
                (float)b.Total, (float)b.Support, (float)b.Strength, (float)b.Contrast, (float)b.Paper, (float)b.Shape, (float)b.Size, (float)b.Border, (float)b.Brighter, (float)Math.Min(b.Aspect, 10),
                (float)(areas[i] / frame), (float)(q.Score - top.Score), (float)Math.Log(Math.Max(areas[i], 1) / Math.Max(topArea, 1)),
                (float)(overlap / Math.Max(topArea, 1)), (float)(overlap / Math.Max(areas[i], 1)),
                grows && QuadFinder.CornersOnOutline(top.Corners, q.Corners) ? 1 : 0,
                grows && QuadFinder.PrintInsidePage(e, top.Corners, q.Corners) ? 1 : 0,
                grows && QuadFinder.ExtraIsPaper(e, top.Corners, q.Corners) ? 1 : 0,
                (float)p.InGray, (float)p.InSat, (float)p.OutGray, (float)p.OutSat, (float)p.InteriorBackground, (float)p.OutbandPaper, (float)p.SideLiftMax, (float)p.SideLiftMin,
                src == "lines" ? 1 : 0, src == "edges" ? 1 : 0, src == "weak-edges" ? 1 : 0, src == "bright" ? 1 : 0, src == "neutral" ? 1 : 0,
                i / (float)scored.Count, largerClose, smallerClose,
                q.Corners.SequenceEqual(rules) ? 1 : 0,
            };
        }
        return result;
    }

    public readonly record struct Photo(double InGray, double InSat, double OutGray, double OutSat, double InteriorBackground, double OutbandPaper, double SideLiftMax, double SideLiftMin);

    /// <summary>
    /// Inside/outside appearance: the margin bands either side of the outline, how much of the inside looks like
    /// the surroundings, how much of the outside looks like the page's margin, and per side how much brighter the
    /// outside is (blank margin outside a block of print).
    /// </summary>
    public static Photo Photometrics(QuadFinder.Evidence e, Point2d[] q)
    {
        var size = e.SmallGray.Size();
        using var mask = new Mat(size, MatType.CV_8UC1, Scalar.All(0));
        Cv2.FillConvexPoly(mask, q.Select(p => new Point((int)Math.Round(p.X / 4), (int)Math.Round(p.Y / 4))).ToArray(), Scalar.All(255));
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * QuadFinder.BandPx + 1, 2 * QuadFinder.BandPx + 1));
        using var core = new Mat();
        using var grown = new Mat();
        Cv2.Erode(mask, core, kernel);
        Cv2.Dilate(mask, grown, kernel);
        using var inBand = new Mat();
        using var outBand = new Mat();
        Cv2.Subtract(mask, core, inBand);
        Cv2.Subtract(grown, mask, outBand);
        double inGray = 0, inSat = 0, outGray = 0, outSat = 0, interiorBg = 0, outbandPaper = 0;
        if (Cv2.CountNonZero(inBand) > 10)
        {
            inGray = Cv2.Mean(e.SmallGray, inBand).Val0;
            inSat = Cv2.Mean(e.SmallSaturation, inBand).Val0;
        }
        if (Cv2.CountNonZero(outBand) > 10)
        {
            outGray = Cv2.Mean(e.SmallGray, outBand).Val0;
            outSat = Cv2.Mean(e.SmallSaturation, outBand).Val0;
            interiorBg = LikeFraction(e, core, outGray, outSat);
            outbandPaper = LikeFraction(e, outBand, inGray, inSat);
        }
        else
        {
            outGray = inGray;
            outSat = inSat;
        }

        // Per side: outside minus inside brightness, a few pixels either side.
        var cx = q.Average(p => p.X);
        var cy = q.Average(p => p.Y);
        double liftMax = double.MinValue, liftMin = double.MaxValue;
        for (var s = 0; s < 4; s++)
        {
            var a = q[s];
            var b = q[(s + 1) % 4];
            var len = Math.Max(Geometry.Distance(a, b), 1);
            var nx = (b.Y - a.Y) / len;
            var ny = -(b.X - a.X) / len;
            if (((a.X + b.X) / 2 - cx) * nx + ((a.Y + b.Y) / 2 - cy) * ny < 0) { nx = -nx; ny = -ny; }
            double inside = 0, outside = 0;
            var n = 0;
            for (var k = 1; k < 16; k++)
            {
                var t = k / 16.0;
                var x = a.X + (b.X - a.X) * t;
                var y = a.Y + (b.Y - a.Y) * t;
                if (!e.Inside(x + nx * 12, y + ny * 12) || !e.Inside(x - nx * 12, y - ny * 12)) continue;
                for (var d = 3; d <= 12; d += 3)
                {
                    inside += e.GrayAt(x - nx * d, y - ny * d);
                    outside += e.GrayAt(x + nx * d, y + ny * d);
                    n++;
                }
            }
            if (n == 0) continue;
            var lift = (outside - inside) / n;
            liftMax = Math.Max(liftMax, lift);
            liftMin = Math.Min(liftMin, lift);
        }
        if (liftMax == double.MinValue) liftMax = liftMin = 0;
        return new Photo(inGray, inSat, outGray, outSat, interiorBg, outbandPaper, liftMax, liftMin);
    }

    static double LikeFraction(QuadFinder.Evidence e, Mat region, double gray, double sat)
    {
        var count = Cv2.CountNonZero(region);
        if (count == 0) return 0;
        using var grayDiff = new Mat();
        using var satDiff = new Mat();
        Cv2.Absdiff(e.SmallGray, new Scalar(gray), grayDiff);
        Cv2.Absdiff(e.SmallSaturation, new Scalar(sat), satDiff);
        using var distance = new Mat();
        Cv2.Add(grayDiff, satDiff, distance);
        using var like = new Mat();
        Cv2.Threshold(distance, like, 18, 255, ThresholdTypes.BinaryInv);
        Cv2.BitwiseAnd(like, region, like);
        return Cv2.CountNonZero(like) / (double)count;
    }

    // ---------------------------------------------------------------- the trained model

    sealed record Tree(int[] Feature, float[] Threshold, int[] Left, int[] Right, float[] Value);

    static readonly Lazy<(Tree[] Trees, float Bias, double Rate)?> Model = new(Load);

    public static bool Available => Model.Value is not null;

    /// <summary>Predicted overlap (IoU) of each candidate with the real page.</summary>
    public static double Predict(float[] features)
    {
        var model = Model.Value ?? throw new InvalidOperationException("no ranker model");
        double sum = model.Bias;
        foreach (var t in model.Trees)
        {
            var node = 0;
            while (t.Left[node] >= 0) node = features[t.Feature[node]] <= t.Threshold[node] ? t.Left[node] : t.Right[node];
            sum += model.Rate * t.Value[node];
        }
        return sum;
    }

    static (Tree[], float, double)? Load()
    {
        using var stream = typeof(PageRanker).Assembly.GetManifestResourceStream("Taildrop.Core.Scanning.page_ranker.json");
        if (stream is null) return null;
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        var names = root.GetProperty("features").EnumerateArray().Select(x => x.GetString()).ToArray();
        if (!names.SequenceEqual(FeatureNames)) throw new InvalidOperationException("ranker model was trained on different features");
        var trees = root.GetProperty("trees").EnumerateArray().Select(t => new Tree(
            t.GetProperty("feature").EnumerateArray().Select(x => x.GetInt32()).ToArray(),
            t.GetProperty("threshold").EnumerateArray().Select(x => x.GetSingle()).ToArray(),
            t.GetProperty("left").EnumerateArray().Select(x => x.GetInt32()).ToArray(),
            t.GetProperty("right").EnumerateArray().Select(x => x.GetInt32()).ToArray(),
            t.GetProperty("value").EnumerateArray().Select(x => x.GetSingle()).ToArray())).ToArray();
        return (trees, root.GetProperty("bias").GetSingle(), root.GetProperty("rate").GetDouble());
    }
}
