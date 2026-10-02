using OpenCvSharp;

namespace Taildrop.Core.Tests;

/// <summary>Finds the synthetic page's red corner markers and straight reference bars in a flattened page.</summary>
static class PageAnalysis
{
    /// <summary>Centres of red blobs, as fractions of width/height.</summary>
    public static List<Point2d> FindMarkers(Mat flat)
    {
        using var bgr = flat.Channels() == 3 ? flat.Clone() : flat.CvtColor(ColorConversionCodes.GRAY2BGR);
        using var mask = new Mat();
        Cv2.InRange(bgr, new Scalar(0, 0, 140), new Scalar(110, 110, 255), mask);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids);
        var minArea = flat.Width * flat.Height * 0.00015;
        var result = new List<Point2d>();
        for (var i = 1; i < count; i++)
        {
            if (stats.At<int>(i, (int)ConnectedComponentsTypes.Area) < minArea) continue;
            result.Add(new Point2d(centroids.At<double>(i, 0) / flat.Width, centroids.At<double>(i, 1) / flat.Height));
        }
        return result;
    }

    /// <summary>Mask of the green reference bars (BGR input).</summary>
    static Mat GreenMask(Mat flat)
    {
        using var bgr = flat.Channels() == 3 ? flat.Clone() : flat.CvtColor(ColorConversionCodes.GRAY2BGR);
        var channels = Cv2.Split(bgr);
        try
        {
            using var gMinusR = new Mat();
            using var gMinusB = new Mat();
            Cv2.Subtract(channels[1], channels[2], gMinusR);
            Cv2.Subtract(channels[1], channels[0], gMinusB);
            var mask = new Mat();
            using var a = new Mat();
            using var b = new Mat();
            Cv2.Threshold(gMinusR, a, 28, 255, ThresholdTypes.Binary);
            Cv2.Threshold(gMinusB, b, 28, 255, ThresholdTypes.Binary);
            Cv2.BitwiseAnd(a, b, mask);
            return mask;
        }
        finally { foreach (var c in channels) c.Dispose(); }
    }

    /// <summary>Vertical centres (fractions of height) of the green reference bars (rows that are mostly green across the page).</summary>
    public static List<double> FindBars(Mat flat)
    {
        using var mask = GreenMask(flat);
        var x0 = (int)(mask.Width * 0.2);
        var x1 = (int)(mask.Width * 0.8);
        var clusters = new List<(double Sum, int Count)>();
        var inBar = false;
        double sum = 0;
        var count = 0;
        for (var y = 0; y < mask.Height; y++)
        {
            var green = 0;
            for (var x = x0; x < x1; x++) if (mask.At<byte>(y, x) > 0) green++;
            var isBar = green >= 0.6 * (x1 - x0);
            if (isBar) { sum += y; count++; inBar = true; }
            else if (inBar) { clusters.Add((sum, count)); sum = 0; count = 0; inBar = false; }
        }
        if (inBar) clusters.Add((sum, count));
        return clusters.Where(c => c.Count >= 2).Select(c => c.Sum / c.Count / mask.Height).ToList();
    }

    /// <summary>
    /// For each expected bar height, the bar's vertical position measured in several column slices across the page.
    /// Returns (mean offset from expected, spread between slices), both as fractions of the page height.
    /// A perfectly flattened page has offset ~0 and spread ~0.
    /// </summary>
    public static List<(double Offset, double Spread)> MeasureBars(Mat flat, IEnumerable<double> expectedFractions)
    {
        using var mask = GreenMask(flat);
        var results = new List<(double, double)>();
        foreach (var expected in expectedFractions)
        {
            var rowMin = Math.Max(0, (int)((expected - 0.05) * mask.Height));
            var rowMax = Math.Min(mask.Height - 1, (int)((expected + 0.05) * mask.Height));
            var centres = new List<double>();
            for (var slice = 0; slice < 8; slice++)
            {
                var x0 = (int)(mask.Width * (0.15 + 0.7 * slice / 8));
                var x1 = (int)(mask.Width * (0.15 + 0.7 * (slice + 1) / 8));
                double weight = 0, moment = 0;
                for (var y = rowMin; y <= rowMax; y++)
                {
                    var green = 0;
                    for (var x = x0; x < x1; x++) if (mask.At<byte>(y, x) > 0) green++;
                    weight += green;
                    moment += green * (double)y;
                }
                if (weight > 0) centres.Add(moment / weight / mask.Height);
            }
            if (centres.Count < 6) results.Add((double.NaN, double.NaN));
            else results.Add((centres.Average() - expected, centres.Max() - centres.Min()));
        }
        return results;
    }

    /// <summary>Intersection over union of two binary masks.</summary>
    public static double Iou(Mat a, Mat b)
    {
        using var intersection = new Mat();
        using var union = new Mat();
        Cv2.BitwiseAnd(a, b, intersection);
        Cv2.BitwiseOr(a, b, union);
        var u = Cv2.CountNonZero(union);
        return u == 0 ? 0 : Cv2.CountNonZero(intersection) / (double)u;
    }

    /// <summary>Rasterizes an outline (corners + curved edges) to a filled mask the size of the photo.</summary>
    public static Mat OutlineMask(Taildrop.Core.Scanning.ScanOutline outline, int width, int height)
    {
        var polygon = new List<Point>();
        void Add(double[][] edge, bool reverse)
        {
            var points = reverse ? edge.Reverse() : edge;
            foreach (var p in points) polygon.Add(new Point((int)Math.Round(p[0] * width), (int)Math.Round(p[1] * height)));
        }
        Add(outline.Top, false);       // TL -> TR
        Add(outline.Right, false);     // TR -> BR
        Add(outline.Bottom, true);     // BR -> BL
        Add(outline.Left, true);       // BL -> TL
        var mask = new Mat(height, width, MatType.CV_8UC1, Scalar.All(0));
        Cv2.FillPoly(mask, new[] { polygon.ToArray() }, Scalar.All(255));
        return mask;
    }
}
