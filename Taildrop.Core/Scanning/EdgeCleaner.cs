using OpenCvSharp;

namespace Taildrop.Core.Scanning;

/// <summary>
/// Makes the flattened page clean at its edges. The outline errs outward so no paper is ever cut off, which can
/// leave a sliver of table along an edge, or the table showing through where a corner is folded over. Starting
/// from each edge of the page, pixels that match what lies just outside the paper there (sampled from the
/// photo) are repainted with the paper's own (local) color, feathered so there is no seam. Binder holes are filled
/// the same way. Print is never touched: only runs that start at the page edge and look like the surroundings,
/// never like paper, are filled.
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
        SheetBeneath(lab, paper.Value, mask);
        HolePunches(lab, paper.Value, mask);
        if (Cv2.CountNonZero(mask) == 0) return;

        // Keep only regions that reach the page border (or are hole punches), closing small gaps; no opening, so
        // thin slivers of table along an edge are kept and cleaned too.
        using var close = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * k + 1, 2 * k + 1));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, close);
        using var grow = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(5, 5));
        Cv2.Dilate(mask, mask, grow);

        // Fill with the paper's local colour: inpainting continues the shading of the paper around the hole, so a
        // dim or unevenly lit page doesn't get bright patches.
        using var filled = new Mat();
        Cv2.Inpaint(work, mask, filled, Math.Max(3, Math.Min(w, h) / 150.0), InpaintTypes.Telea);
        using var smallAlpha = new Mat();
        mask.ConvertTo(smallAlpha, MatType.CV_32F, 1 / 255.0);
        Cv2.GaussianBlur(smallAlpha, smallAlpha, new Size(0, 0), 1.5);
        using var alpha = new Mat();
        Cv2.Resize(smallAlpha, alpha, page.Size(), 0, 0, InterpolationFlags.Linear);
        using var fillImage = new Mat();
        Cv2.Resize(filled, fillImage, page.Size(), 0, 0, InterpolationFlags.Linear);
        using var keep = new Mat();
        Cv2.Subtract(Scalar.All(1), alpha, keep);
        using var blended = new Mat();
        Cv2.BlendLinear(page, fillImage, keep, alpha, blended);
        blended.CopyTo(page);
    }

    /// <summary>
    /// Another sheet showing between the page's own edge and the outline (a packet, a sheet underneath, a torn-off
    /// corner showing the page below). The page edge is traced inside each side of the border with dynamic
    /// programming, and the strip outside it is filled where it is clearly not this page: darker, a different tint,
    /// or more textured (grid lines). A blank margin outside a line of print is the page itself, so it is kept.
    /// </summary>
    static void SheetBeneath(Mat lab, Vec3d paper, Mat mask)
    {
        var w = lab.Width;
        var h = lab.Height;
        using var lSmooth = new Mat();
        Cv2.ExtractChannel(lab, lSmooth, 0);
        Cv2.GaussianBlur(lSmooth, lSmooth, new Size(0, 0), 1.5);
        var L = lSmooth.GetGenericIndexer<byte>();
        var px = lab.GetGenericIndexer<Vec3b>();
        var mx = mask.GetGenericIndexer<byte>();
        for (var side = 0; side < 4; side++)
        {
            var along = side is 0 or 2 ? w : h;
            var across = side is 0 or 2 ? h : w;
            var dMax = Math.Max(6, (int)(across * SheetDepth));
            (int X, int Y) At(int j, int d) => side switch { 0 => (j, d), 1 => (w - 1 - d, j), 2 => (j, h - 1 - d), _ => (d, j) };
            byte Lum(int j, int d) { var (x, y) = At(j, Math.Clamp(d, 0, across - 1)); return L[y, x]; }

            // Unary: reward a step from the darker or different outside to the page inside.
            const int Step = 2;
            var n = (along + Step - 1) / Step;
            var cost = new double[n, dMax];
            var from = new int[n, dMax];
            for (var i = 0; i < n; i++)
            {
                var j = i * Step;
                for (var d = 2; d < dMax; d++)
                {
                    var step = Lum(j, d + 2) - Lum(j, d - 2);           // brighter inside
                    var u = -Math.Clamp(Math.Abs(step), 0, 25) / 25.0;
                    var best = i == 0 ? 0 : double.MaxValue;
                    var arg = d;
                    if (i > 0)
                        for (var p = Math.Max(2, d - 4); p <= Math.Min(dMax - 1, d + 4); p++)
                        {
                            var c = cost[i - 1, p] + 0.03 * (d - p) * (d - p);
                            if (c < best) { best = c; arg = p; }
                        }
                    cost[i, d] = best + u;
                    from[i, d] = arg;
                }
                cost[i, 0] = cost[i, 1] = double.MaxValue / 4;
            }
            var path = new int[n];
            var last = 2;
            for (var d = 3; d < dMax; d++) if (cost[n - 1, d] < cost[n - 1, last]) last = d;
            path[n - 1] = last;
            for (var i = n - 1; i > 0; i--) path[i - 1] = from[i, path[i]];

            // Accept a column where the strip outside is clearly another surface than the page just inside.
            var accept = new bool[n];
            for (var i = 0; i < n; i++)
            {
                var j = i * Step;
                var d0 = path[i];
                if (d0 < 3) continue;
                var outside = Stats(px, At, j, 0, d0 - 2);
                var inside = Stats(px, At, j, d0 + 2, Math.Min(across - 1, d0 + 2 + Math.Max(6, d0)));
                // The page just inside must be plain paper (not a photo print, not dense print).
                var paperInside = inside.L > paper.Item0 - 25 && inside.Spread < 14;
                var darker = inside.L - outside.L > 6;
                var tint = Math.Sqrt(Math.Pow(inside.A - outside.A, 2) + Math.Pow(inside.B - outside.B, 2)) > 4;
                var texture = outside.Spread > inside.Spread + 6;
                var edge = Math.Abs(Lum(j, d0 + 2) - Lum(j, d0 - 2)) >= 5;
                if (!paperInside) continue;
                accept[i] = edge && (darker || tint || texture);
            }
            // Only runs of at least 4% of the side: a stray column next to print is not another sheet.
            var minRun = Math.Max(3, (int)(n * 0.08));
            for (var i = 0; i < n;)
            {
                if (!accept[i]) { i++; continue; }
                var e = i;
                while (e < n && accept[e]) e++;
                if (e - i >= minRun && Straight(path, i, e))
                    for (var k = i; k < e; k++)
                        for (var jj = k * Step; jj < Math.Min(along, k * Step + Step); jj++)
                            for (var d = 0; d <= path[k]; d++)
                            {
                                var (x, y) = At(jj, d);
                                mx[y, x] = 255;
                            }
                i = e;
            }
        }
    }

    internal static double SheetDepth = 0.15;

    /// <summary>A sheet's edge (or a cut corner) is a straight line; handwriting and print are not.</summary>
    static bool Straight(int[] path, int from, int to)
    {
        var pts = Enumerable.Range(from, to - from).Select(i => new Point2f(i, path[i])).ToArray();
        var line = Cv2.FitLine(pts, DistanceTypes.L2, 0, 0.01, 0.01);
        var rms = Math.Sqrt(pts.Average(p =>
        {
            var dx = p.X - line.X1;
            var dy = p.Y - line.Y1;
            var cross = dx * line.Vy - dy * line.Vx;
            return cross * cross;
        }));
        return rms < 1.2;
    }

    /// <summary>Median L, a, b over a strip (robust to the odd pen stroke) and the spread of L.</summary>
    static (double L, double A, double B, double Spread) Stats(MatIndexer<Vec3b> px, Func<int, int, (int X, int Y)> at, int j, int d0, int d1)
    {
        if (d1 < d0) return (0, 128, 128, 0);
        var ls = new List<double>();
        var As = new List<double>();
        var bs = new List<double>();
        for (var d = d0; d <= d1; d++)
        {
            var (x, y) = at(j, d);
            var v = px[y, x];
            ls.Add(v.Item0); As.Add(v.Item1); bs.Add(v.Item2);
        }
        double Median(List<double> v) { v.Sort(); return v[v.Count / 2]; }
        var mean = ls.Average();
        var spread = Math.Sqrt(ls.Average(v => (v - mean) * (v - mean)));
        return (Median(ls), Median(As), Median(bs), spread);
    }

    /// <summary>
    /// Binder holes: two or more round, dark, equal-sized blobs in a row near one side of the page. They are added
    /// to the mask so they are filled like any other place the table shows through. Printed dots and letters are
    /// not round enough, not dark enough, or not lined up along a side.
    /// </summary>
    static void HolePunches(Mat lab, Vec3d paper, Mat mask)
    {
        var w = lab.Width;
        var h = lab.Height;
        var shortSide = Math.Min(w, h);
        using var l = new Mat();
        Cv2.ExtractChannel(lab, l, 0);
        using var dark = new Mat();
        Cv2.Threshold(l, dark, Math.Max(20, paper.Item0 - 70), 255, ThresholdTypes.BinaryInv);
        // Opening with a disc a bit smaller than a hole removes print strokes and rules (thin), keeping holes even
        // where they touch a line of print.
        using var rawDark = dark.Clone();
        var disc = Math.Max(3, (int)(0.017 * shortSide)) | 1;
        using var discKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(disc, disc));
        Cv2.MorphologyEx(dark, dark, MorphTypes.Open, discKernel);
        Cv2.FindContours(dark, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        // Hole punches are about 6-7 mm on a 216 mm wide sheet: 2-4.5% of the short side.
        var holes = new List<(Point2f Center, float Radius, Point[] Contour)>();
        foreach (var c in contours)
        {
            var area = Cv2.ContourArea(c);
            Cv2.MinEnclosingCircle(c, out var center, out var radius);
            var d = 2 * radius / shortSide;
            if (d < 0.02 || d > 0.05) continue;
            var circularity = area / (Math.PI * radius * radius);
            if (circularity < 0.7) continue;
            // Judge the shape before the opening rounded it off: the original dark area around the blob must be a
            // round disc (4*pi*area/perimeter^2 about 0.85-0.9) and fill its circle (a hole ~0.85, a square ~0.64-0.7).
            using var near = new Mat(rawDark.Size(), MatType.CV_8UC1, Scalar.All(0));
            Cv2.Circle(near, new Point((int)center.X, (int)center.Y), (int)(radius * 1.1) + 1, Scalar.All(255), -1);
            Cv2.BitwiseAnd(rawDark, near, near);
            Cv2.FindContours(near, out var rawContours, out _, RetrievalModes.External, ContourApproximationModes.ApproxNone);
            var raw = rawContours.OrderByDescending(r => Cv2.ContourArea(r)).FirstOrDefault();
            if (raw is null) continue;
            var rawArea = Cv2.ContourArea(raw);
            var perimeter = Cv2.ArcLength(raw, true);
            if (perimeter <= 0 || 4 * Math.PI * rawArea / (perimeter * perimeter) < 0.8) continue;
            Cv2.MinEnclosingCircle(raw, out _, out var rawRadius);
            if (rawArea / (Math.PI * rawRadius * rawRadius) < 0.78) continue;
            var nearSide = Math.Min(Math.Min(center.X, w - center.X), Math.Min(center.Y, h - center.Y)) < 0.12 * shortSide;
            if (nearSide) holes.Add((center, radius, c));
        }
        // Keep the ones that have a partner of similar size lined up along the same side.
        foreach (var hole in holes)
        {
            var partnered = holes.Any(o => !ReferenceEquals(o.Contour, hole.Contour)
                && Math.Abs(o.Radius - hole.Radius) < 0.25 * hole.Radius
                && (Math.Abs(o.Center.X - hole.Center.X) < 0.03 * shortSide || Math.Abs(o.Center.Y - hole.Center.Y) < 0.03 * shortSide));
            // A lone hole must be unmistakable (punched paper is a clean circle).
            if (!partnered && Cv2.ContourArea(hole.Contour) / (Math.PI * hole.Radius * hole.Radius) < 0.85) continue;
            Cv2.Circle(mask, new Point((int)hole.Center.X, (int)hole.Center.Y), (int)Math.Ceiling(hole.Radius * 1.25) + 1, Scalar.All(255), -1);
        }
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
