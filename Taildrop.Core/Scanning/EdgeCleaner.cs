using OpenCvSharp;

namespace Taildrop.Core.Scanning;

/// <summary>
/// Makes the flattened page clean at its edges. The outline errs outward so no paper is ever cut off, which can
/// leave a sliver of table along an edge, or the table showing through where a corner is folded over. Starting
/// from each edge of the page, pixels that match what lies just outside the paper there (sampled from the
/// photo) are repainted with the paper's own color, feathered so there is no seam. Print is never touched: only
/// runs that start at the page edge and look like the surroundings, never like paper, are filled.
/// </summary>
static class EdgeCleaner
{
    /// <summary>How far in from an edge (fraction of the page's size) a run of "not paper" may reach.</summary>
    internal static double MaxDepth = 0.12;
    /// <summary>Lab distance below which a page pixel counts as the surroundings.</summary>
    internal static double Tolerance = 14;
    internal static bool Enabled = true;

    public static void Clean(Mat photo, ScanOutline outline, Mat page)
    {
        if (!Enabled || page.Width < 32 || page.Height < 32) return;
        // Search at about 1200 px; the mask is scaled back up for the (feathered) fill.
        var scale = Math.Min(1.0, 1200.0 / Math.Max(page.Width, page.Height));
        using var work = new Mat();
        Cv2.Resize(page, work, new Size(), scale, scale, InterpolationFlags.Area);
        var w = work.Width;
        var h = work.Height;
        using var lab = new Mat();
        Cv2.CvtColor(work, lab, ColorConversionCodes.BGR2Lab);
        // Smoothed copy for the comparisons, so the grain of wood or cloth doesn't break a run up.
        using var smooth = new Mat();
        var k = 5;
        Cv2.MedianBlur(lab, smooth, k);
        var center = new Point2d(outline.Corners.Average(c => c[0]) * photo.Width, outline.Corners.Average(c => c[1]) * photo.Height);
        var diag = Math.Sqrt(photo.Width * (double)photo.Width + photo.Height * (double)photo.Height);
        var paper = PaperColor(lab);
        if (paper is null) return;
        var px = smooth.GetGenericIndexer<Vec3b>();

        using var mask = new Mat(h, w, MatType.CV_8UC1, Scalar.All(0));
        var mx = mask.GetGenericIndexer<byte>();
        // side: 0 top (rows go down), 1 right (cols go left), 2 bottom (rows go up), 3 left (cols go right)
        for (var side = 0; side < 4; side++)
        {
            var edge = side switch { 0 => outline.Top, 1 => outline.Right, 2 => outline.Bottom, _ => outline.Left };
            var along = side is 0 or 2 ? w : h;
            var depthMax = (int)Math.Round((side is 0 or 2 ? h : w) * MaxDepth);
            const int Samples = 160;
            var outsideAt = new Vec3d?[Samples + 1];
            for (var i = 0; i <= Samples; i++) outsideAt[i] = Surroundings(photo, edge, i / (double)Samples, center, diag);
            for (var j = 0; j < along; j++)
            {
                var outside = outsideAt[(int)Math.Round(j / (double)Math.Max(along - 1, 1) * Samples)];
                if (outside is null) continue;
                // Never clean where the surroundings look like paper (white table, another sheet).
                if (Distance(outside.Value, paper.Value) < 2 * Tolerance) continue;
                var paperRun = 0;
                var last = -1;
                for (var d = 0; d < depthMax; d++)
                {
                    var (x, y) = side switch { 0 => (j, d), 1 => (w - 1 - d, j), 2 => (j, h - 1 - d), _ => (d, j) };
                    var v = px[y, x];
                    var here = new Vec3d(v.Item0, v.Item1, v.Item2);
                    var toOutside = Distance(here, outside.Value);
                    if (toOutside < Distance(here, paper.Value) && toOutside < 3 * Tolerance) { paperRun = 0; last = d; }
                    else if (++paperRun > 3) break;
                }
                for (var d = 0; d <= last; d++)
                {
                    var (x, y) = side switch { 0 => (j, d), 1 => (w - 1 - d, j), 2 => (j, h - 1 - d), _ => (d, j) };
                    mx[y, x] = 255;
                }
            }
        }
        if (Cv2.CountNonZero(mask) == 0) return;

        // Tidy the mask (single-column runs are noise), then feather it and blend the paper color in.
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(5, 5));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Open, kernel);
        using var close = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * k + 1, 2 * k + 1));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, close);
        Cv2.Dilate(mask, mask, kernel);
        using var smallAlpha = new Mat();
        mask.ConvertTo(smallAlpha, MatType.CV_32F, 1 / 255.0);
        Cv2.GaussianBlur(smallAlpha, smallAlpha, new Size(0, 0), 1.5);
        using var alpha = new Mat();
        Cv2.Resize(smallAlpha, alpha, page.Size(), 0, 0, InterpolationFlags.Linear);
        w = page.Width;
        h = page.Height;
        using var paperLab = new Mat(1, 1, MatType.CV_8UC3, new Scalar(paper.Value.Item0, paper.Value.Item1, paper.Value.Item2));
        using var paperBgr = new Mat();
        Cv2.CvtColor(paperLab, paperBgr, ColorConversionCodes.Lab2BGR);
        var fill = paperBgr.At<Vec3b>(0, 0);
        using var fillImage = new Mat(h, w, MatType.CV_8UC3, new Scalar(fill.Item0, fill.Item1, fill.Item2));
        using var keep = new Mat();
        Cv2.Subtract(Scalar.All(1), alpha, keep);
        using var blended = new Mat();
        Cv2.BlendLinear(page, fillImage, keep, alpha, blended);
        blended.CopyTo(page);
    }

    /// <summary>Paper color: the median of the brighter half of a band just inside the page edges.</summary>
    static Vec3d? PaperColor(Mat lab)
    {
        var w = lab.Width;
        var h = lab.Height;
        var inset = Math.Max(2, (int)(Math.Min(w, h) * 0.04));
        var samples = new List<Vec3b>();
        var step = Math.Max(1, (w + h) / 400);
        for (var x = 0; x < w; x += step) { samples.Add(lab.At<Vec3b>(inset, x)); samples.Add(lab.At<Vec3b>(h - 1 - inset, x)); }
        for (var y = 0; y < h; y += step) { samples.Add(lab.At<Vec3b>(y, inset)); samples.Add(lab.At<Vec3b>(y, w - 1 - inset)); }
        if (samples.Count < 10) return null;
        var bright = samples.OrderByDescending(s => s.Item0).Take(samples.Count / 2).ToList();
        double Median(IEnumerable<double> v) { var a = v.OrderBy(x => x).ToArray(); return a[a.Length / 2]; }
        return new Vec3d(Median(bright.Select(s => (double)s.Item0)), Median(bright.Select(s => (double)s.Item1)), Median(bright.Select(s => (double)s.Item2)));
    }

    /// <summary>Lab color just outside the outline at parameter t along an edge (averaged over a few pixels), or null off-frame.</summary>
    static Vec3d? Surroundings(Mat photo, double[][] edge, double t, Point2d center, double diag)
    {
        var f = t * (edge.Length - 1);
        var i = Math.Min((int)f, edge.Length - 2);
        var frac = f - i;
        var x = (edge[i][0] + (edge[i + 1][0] - edge[i][0]) * frac) * photo.Width;
        var y = (edge[i][1] + (edge[i + 1][1] - edge[i][1]) * frac) * photo.Height;
        var tx = (edge[i + 1][0] - edge[i][0]) * photo.Width;
        var ty = (edge[i + 1][1] - edge[i][1]) * photo.Height;
        var len = Math.Sqrt(tx * tx + ty * ty);
        if (len < 1e-9) return null;
        var nx = ty / len;
        var ny = -tx / len;
        if ((x - center.X) * nx + (y - center.Y) * ny < 0) { nx = -nx; ny = -ny; }
        double l = 0, a = 0, b = 0;
        var n = 0;
        foreach (var offset in new[] { 0.012, 0.018, 0.025 })
        {
            var sx = (int)Math.Round(x + nx * offset * diag);
            var sy = (int)Math.Round(y + ny * offset * diag);
            if (sx < 0 || sy < 0 || sx >= photo.Width || sy >= photo.Height) continue;
            var bgr = photo.At<Vec3b>(sy, sx);
            var c = ToLab(bgr);
            l += c.Item0; a += c.Item1; b += c.Item2;
            n++;
        }
        return n == 0 ? null : new Vec3d(l / n, a / n, b / n);
    }

    static Vec3d ToLab(Vec3b bgr)
    {
        using var one = new Mat(1, 1, MatType.CV_8UC3, new Scalar(bgr.Item0, bgr.Item1, bgr.Item2));
        using var lab = new Mat();
        Cv2.CvtColor(one, lab, ColorConversionCodes.BGR2Lab);
        var v = lab.At<Vec3b>(0, 0);
        return new Vec3d(v.Item0, v.Item1, v.Item2);
    }

    static double Distance(Vec3d p, Vec3d q)
    {
        double dl = (p.Item0 - q.Item0) * 0.5, da = p.Item1 - q.Item1, db = p.Item2 - q.Item2; // lightness weighs less: shading
        return Math.Sqrt(dl * dl + da * da + db * db);
    }
}
