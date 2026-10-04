using OpenCvSharp;

namespace Taildrop.Core.Scanning;

/// <summary>
/// Proposes and scores page quadrilaterals. Two independent sources feed it: straight-line hypotheses from
/// detected line segments (robust to clutter crossing an edge, to other sheets underneath and to low contrast),
/// and the outlines of paper-like regions from <see cref="DocumentDetector"/>. Every candidate is judged by the
/// same evidence: is there an edge all the way round, does the paper/background contrast stay consistent, does
/// the inside look like paper, and is the shape plausible.
/// </summary>
static class QuadFinder
{
    public sealed record Quad(Point2d[] Corners, double Score, string Source);

    /// <summary>Gradient images (working resolution) shared by scoring.</summary>
    public sealed class Evidence : IDisposable
    {
        public Mat Gray { get; } = new();
        public Mat Gx { get; } = new();
        public Mat Gy { get; } = new();
        public Mat Saturation { get; } = new();
        /// <summary>Quarter-size copies for cheap inside/outside statistics.</summary>
        public Mat SmallGray { get; } = new();
        public Mat SmallSaturation { get; } = new();
        public int Width => Gray.Width;
        public int Height => Gray.Height;

        public Evidence(Mat bgr)
        {
            using var g = bgr.CvtColor(ColorConversionCodes.BGR2GRAY);
            Cv2.GaussianBlur(g, Gray, new Size(0, 0), 1.2);
            Cv2.Sobel(Gray, Gx, MatType.CV_32F, 1, 0, 3, 0.25);
            Cv2.Sobel(Gray, Gy, MatType.CV_32F, 0, 1, 3, 0.25);
            using var hsv = bgr.CvtColor(ColorConversionCodes.BGR2HSV);
            Cv2.ExtractChannel(hsv, Saturation, 1);
            Cv2.Resize(Gray, SmallGray, new Size(), 0.25, 0.25, InterpolationFlags.Area);
            Cv2.Resize(Saturation, SmallSaturation, new Size(), 0.25, 0.25, InterpolationFlags.Area);
            // Scoring reads these pixel by pixel, millions of times per photo: managed copies avoid a native call per read.
            _gray = new byte[Width * Height];
            _gx = new float[Width * Height];
            _gy = new float[Width * Height];
            for (var y = 0; y < Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(Gray.Ptr(y), _gray, y * Width, Width);
                System.Runtime.InteropServices.Marshal.Copy(Gx.Ptr(y), _gx, y * Width, Width);
                System.Runtime.InteropServices.Marshal.Copy(Gy.Ptr(y), _gy, y * Width, Width);
            }
        }

        readonly byte[] _gray;
        readonly float[] _gx;
        readonly float[] _gy;

        public void Dispose()
        {
            Gray.Dispose(); Gx.Dispose(); Gy.Dispose(); Saturation.Dispose(); SmallGray.Dispose(); SmallSaturation.Dispose();
        }

        public bool Inside(double x, double y) => x >= 1 && y >= 1 && x < Width - 2 && y < Height - 2;

        public float GrayAt(double x, double y) => _gray[Math.Clamp((int)Math.Round(y), 0, Height - 1) * Width + Math.Clamp((int)Math.Round(x), 0, Width - 1)];

        /// <summary>Directional derivative along (nx, ny), best within +-2 px along that direction.</summary>
        public double Along(double x, double y, double nx, double ny)
        {
            double best = 0;
            for (var k = -2; k <= 2; k++)
            {
                var px = (int)Math.Round(x + nx * k);
                var py = (int)Math.Round(y + ny * k);
                if (px < 0 || py < 0 || px >= Width || py >= Height) continue;
                var i = py * Width + px;
                var d = _gx[i] * nx + _gy[i] * ny;
                if (Math.Abs(d) > Math.Abs(best)) best = d;
            }
            return best;
        }
    }

    // ---------------------------------------------------------------- line-based hypotheses

    sealed record Line(Point2d A, Point2d B, double Length)
    {
        // Computed once: the collinear merge compares every pair of segments, which is thousands of times per photo.
        public Point2d Direction { get; } = new((B.X - A.X) / Length, (B.Y - A.Y) / Length);
        public double Angle { get; } = Math.Atan2(B.Y - A.Y, B.X - A.X);
    }

    public static List<Point2d[]> LineHypotheses(Mat bgr, Evidence evidence)
    {
        var diag = Math.Sqrt(bgr.Width * (double)bgr.Width + bgr.Height * (double)bgr.Height);
        using var gray = bgr.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var clahe = Cv2.CreateCLAHE(2.5, new Size(8, 8));
        using var enhanced = new Mat();
        clahe.Apply(gray, enhanced);

        using var lsd = LineSegmentDetector.Create(LineSegmentDetectorModes.RefineStd, 0.8, 0.6, 2.0, 22.5, 0, 0.7, 1024);
        lsd.Detect(enhanced, out Vec4f[] raw, out _, out _, out _);
        if (UseSaturationLines)
        {
            // White paper on a light but tinted surface (wood, a pink desk) differs more in color than in brightness.
            using var saturation = new Mat();
            Cv2.GaussianBlur(evidence.Saturation, saturation, new Size(0, 0), SaturationBlur);
            using var satEnhanced = new Mat();
            clahe.Apply(saturation, satEnhanced);
            lsd.Detect(satEnhanced, out Vec4f[] colorLines, out _, out _, out _);
            raw = raw.Concat(colorLines).ToArray();
        }

        var segments = raw
            .Select(s => new Line(new Point2d(s.Item0, s.Item1), new Point2d(s.Item2, s.Item3),
                Math.Sqrt((s.Item2 - s.Item0) * (s.Item2 - s.Item0) + (s.Item3 - s.Item1) * (s.Item3 - s.Item1))))
            .Where(l => l.Length > MinSegment * diag)
            .OrderByDescending(l => l.Length)
            .ToList();

        // Merge collinear pieces (an edge broken by a cable or a pen is still one edge).
        var lines = new List<(Line Line, double Support)>();
        foreach (var s in segments)
        {
            var merged = false;
            for (var i = 0; i < lines.Count; i++)
            {
                var l = lines[i].Line;
                if (!Collinear(l, s, diag)) continue;
                lines[i] = (Extend(l, s), lines[i].Support + s.Length);
                merged = true;
                break;
            }
            if (!merged) lines.Add((s, s.Length));
        }
        // Rank by length and by how clearly the line separates a lighter side (paper) from a darker one: long
        // table edges and cables otherwise crowd out the sides of a sheet on a cluttered desk.
        var top = lines
            .Where(l => l.Line.Length > 0.035 * diag && l.Support > 0.035 * diag)
            .Select(l => (l.Line, Rank: l.Support * (0.4 + Math.Min(1, Step(evidence, l.Line) / 25.0))))
            .OrderByDescending(l => l.Rank)
            .Take(LinePool)
            .Select(l => l.Line)
            .ToList();

        LastLines = top.Select(l => (l.A, l.B)).ToList();
        var quads = new List<Point2d[]>();
        var n = top.Count;
        for (var a = 0; a < n; a++)
        for (var b = a + 1; b < n; b++)
        for (var c = b + 1; c < n; c++)
        for (var d = c + 1; d < n; d++)
        {
            var four = new[] { top[a], top[b], top[c], top[d] };
            var quad = QuadFromLines(four, bgr.Width, bgr.Height);
            if (quad is not null) quads.Add(quad);
        }
        return quads;
    }

    internal static int LinePool = 26;
    internal static bool UseSaturationLines = true;
    internal static double SaturationBlur = 2.5;
    /// <summary>Short pieces still count: a faint edge often comes out of the line detector broken up.</summary>
    internal static double MinSegment = 0.015;
    internal static double MaxGap = 0.08;
    [ThreadStatic] internal static List<(Point2d A, Point2d B)>? LastLines;

    /// <summary>Mean absolute brightness difference across the line (a few pixels either side).</summary>
    static double Step(Evidence e, Line l)
    {
        var d = l.Direction;
        double sum = 0;
        var n = 0;
        for (var i = 1; i < 20; i++)
        {
            var t = i / 20.0;
            var x = l.A.X + (l.B.X - l.A.X) * t;
            var y = l.A.Y + (l.B.Y - l.A.Y) * t;
            if (!e.Inside(x + d.Y * 6, y - d.X * 6) || !e.Inside(x - d.Y * 6, y + d.X * 6)) continue;
            sum += Math.Abs(e.GrayAt(x + d.Y * 5, y - d.X * 5) - e.GrayAt(x - d.Y * 5, y + d.X * 5));
            n++;
        }
        return n == 0 ? 0 : sum / n;
    }

    static bool Collinear(Line l, Line s, double diag)
    {
        var angle = Math.Abs(NormalizeAngle(l.Angle - s.Angle));
        if (angle > 3.0 * Math.PI / 180) return false;
        var d = l.Direction;
        double Dist(Point2d p) => Math.Abs((p.X - l.A.X) * d.Y - (p.Y - l.A.Y) * d.X);
        if (Dist(s.A) >= 0.006 * diag || Dist(s.B) >= 0.006 * diag) return false;
        // ... and close enough along the line that it is one broken edge, not two edges that happen to line up.
        double T(Point2d p) => (p.X - l.A.X) * d.X + (p.Y - l.A.Y) * d.Y;
        var lo = Math.Min(T(s.A), T(s.B));
        var hi = Math.Max(T(s.A), T(s.B));
        var gap = Math.Max(0, Math.Max(lo - l.Length, -hi));
        return gap < MaxGap * diag;
    }

    static Line Extend(Line l, Line s)
    {
        var d = l.Direction;
        var pts = new[] { l.A, l.B, s.A, s.B };
        var ts = pts.Select(p => (p.X - l.A.X) * d.X + (p.Y - l.A.Y) * d.Y).ToArray();
        var lo = ts.Min();
        var hi = ts.Max();
        var a = new Point2d(l.A.X + d.X * lo, l.A.Y + d.Y * lo);
        var b = new Point2d(l.A.X + d.X * hi, l.A.Y + d.Y * hi);
        return new Line(a, b, hi - lo);
    }

    static double NormalizeAngle(double a)
    {
        while (a > Math.PI / 2) a -= Math.PI;
        while (a < -Math.PI / 2) a += Math.PI;
        return a;
    }

    /// <summary>Four lines -> convex quad, if two of them can be "opposite" pairs and the corners land near the image.</summary>
    static Point2d[]? QuadFromLines(Line[] lines, int width, int height)
    {
        // Pair lines into two opposite pairs: the pairing whose pairs are most parallel.
        int[][] pairings = { new[] { 0, 1, 2, 3 }, new[] { 0, 2, 1, 3 }, new[] { 0, 3, 1, 2 } };
        double bestScore = double.MaxValue;
        int[]? best = null;
        foreach (var p in pairings)
        {
            var d1 = Math.Abs(NormalizeAngle(lines[p[0]].Angle - lines[p[1]].Angle));
            var d2 = Math.Abs(NormalizeAngle(lines[p[2]].Angle - lines[p[3]].Angle));
            var cross = Math.Abs(NormalizeAngle(lines[p[0]].Angle - lines[p[2]].Angle));
            if (d1 > 0.6 || d2 > 0.6 || cross < 0.5) continue;
            if (d1 + d2 < bestScore) { bestScore = d1 + d2; best = p; }
        }
        if (best is null) return null;
        // Cyclic order: line0, line2, line1, line3 (opposites never adjacent).
        var order = new[] { lines[best[0]], lines[best[2]], lines[best[1]], lines[best[3]] };
        var corners = new Point2d[4];
        for (var i = 0; i < 4; i++)
        {
            var p = Intersect(order[i], order[(i + 1) % 4]);
            if (p is null) return null;
            var c = p.Value;
            if (c.X < -0.08 * width || c.Y < -0.08 * height || c.X > 1.08 * width || c.Y > 1.08 * height) return null;
            corners[i] = c;
        }
        if (!IsConvex(corners)) return null;
        var area = Math.Abs(Geometry.SignedArea(corners));
        if (area < 0.025 * width * height || area > 0.985 * width * height) return null;
        return corners;
    }

    static Point2d? Intersect(Line a, Line b)
    {
        var d1 = a.Direction;
        var d2 = b.Direction;
        var den = d1.X * d2.Y - d1.Y * d2.X;
        if (Math.Abs(den) < 1e-6) return null;
        var t = ((b.A.X - a.A.X) * d2.Y - (b.A.Y - a.A.Y) * d2.X) / den;
        return new Point2d(a.A.X + d1.X * t, a.A.Y + d1.Y * t);
    }

    public static bool IsConvex(Point2d[] q)
    {
        var sign = 0;
        for (var i = 0; i < q.Length; i++)
        {
            var a = q[i];
            var b = q[(i + 1) % q.Length];
            var c = q[(i + 2) % q.Length];
            var cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            var s = Math.Sign(cross);
            if (s == 0) continue;
            if (sign == 0) sign = s;
            else if (s != sign) return false;
        }
        return true;
    }

    // ---------------------------------------------------------------- scoring

    public readonly record struct Breakdown(double Total, double Support, double Strength, double Contrast, double Paper, double Shape, double Size, double Border, double Brighter = 0, double Aspect = 0);

    /// <summary>How much this quadrilateral looks like the outline of a sheet of paper, 0-1.</summary>
    public static Breakdown Score(Evidence e, Point2d[] q, bool withInterior = true)
    {
        var w = e.Width;
        var h = e.Height;
        var area = Math.Abs(Geometry.SignedArea(q));
        var areaRatio = area / (w * (double)h);
        if (areaRatio < 0.02 || areaRatio > 0.99 || !IsConvex(q)) return default;

        var cx = q.Average(p => p.X);
        var cy = q.Average(p => p.Y);

        // Walk the perimeter: edge evidence along the outward normal and inside/outside brightness.
        const int perSide = 48;
        int samples = 0, supported = 0, onBorder = 0, brighterInside = 0, darkerInside = 0;
        double strength = 0;
        for (var side = 0; side < 4; side++)
        {
            var a = q[side];
            var b = q[(side + 1) % 4];
            var len = Geometry.Distance(a, b);
            var tx = (b.X - a.X) / len;
            var ty = (b.Y - a.Y) / len;
            var nx = ty;
            var ny = -tx;
            // make the normal point outward
            var mx = (a.X + b.X) / 2;
            var my = (a.Y + b.Y) / 2;
            if ((mx - cx) * nx + (my - cy) * ny < 0) { nx = -nx; ny = -ny; }
            for (var i = 1; i < perSide; i++)
            {
                var t = i / (double)perSide;
                var x = a.X + (b.X - a.X) * t;
                var y = a.Y + (b.Y - a.Y) * t;
                if (!e.Inside(x, y))
                {
                    onBorder++;
                    continue;
                }
                samples++;
                var d = e.Along(x, y, nx, ny);
                strength += Math.Min(Math.Abs(d), 30);
                if (Math.Abs(d) > 4.5) supported++;
                var inside = e.GrayAt(x - nx * 5, y - ny * 5);
                var outside = e.GrayAt(x + nx * 5, y + ny * 5);
                if (inside - outside > 6) brighterInside++;
                else if (outside - inside > 6) darkerInside++;
            }
        }
        var total = samples + onBorder;
        var border = onBorder / (double)Math.Max(total, 1);
        if (samples < 20) return default;
        var support = supported / (double)samples;
        var strengthScore = Math.Clamp(strength / samples / 18.0, 0, 1);
        var contrast = Math.Max(brighterInside, darkerInside) / (double)samples;
        var polarityBonus = brighterInside >= darkerInside ? 1.0 : 0.75; // paper is usually the brighter side

        // Paper: the band just inside the outline is a page's margin, bright and low-saturation, and lighter than
        // the band just outside. Measuring the band rather than the whole inside means a photo printed on the page
        // doesn't make the page look less like paper than a block of its own text does.
        double paper = 0.5;
        if (withInterior)
        {
            using var mask = new Mat(e.SmallGray.Rows, e.SmallGray.Cols, MatType.CV_8UC1, Scalar.All(0));
            Cv2.FillConvexPoly(mask, q.Select(p => new Point((int)Math.Round(p.X / 4), (int)Math.Round(p.Y / 4))).ToArray(), Scalar.All(255));
            using var inner = new Mat();
            using var outer = new Mat();
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * BandPx + 1, 2 * BandPx + 1));
            Cv2.Erode(mask, inner, kernel);
            Cv2.Dilate(mask, outer, kernel);
            using var inBand = new Mat();
            using var outBand = new Mat();
            Cv2.Subtract(mask, inner, inBand);
            Cv2.Subtract(outer, mask, outBand);
            var source = Cv2.CountNonZero(inBand) > 30 ? inBand : mask;
            var meanIn = Cv2.Mean(e.SmallGray, source).Val0;
            var satIn = Cv2.Mean(e.SmallSaturation, source).Val0;
            var meanOut = Cv2.CountNonZero(outBand) > 30 ? Cv2.Mean(e.SmallGray, outBand).Val0 : meanIn;
            paper = Math.Clamp((meanIn - 90) / 110.0, 0, 1) * Math.Clamp(1 - (satIn - 40) / 120.0, 0, 1);
            paper = 0.7 * paper + 0.3 * Math.Clamp((meanIn - meanOut + 10) / 50.0, 0, 1);
        }

        // Shape: corner angles and aspect ratio of a photographed sheet.
        double shape = 1;
        for (var i = 0; i < 4; i++)
        {
            var angle = CornerAngle(q[(i + 3) % 4], q[i], q[(i + 1) % 4]);
            var off = Math.Max(0, Math.Max(45 - angle, angle - 135));
            shape = Math.Min(shape, Math.Clamp(1 - off / 25, 0, 1));
        }
        var s1 = (Geometry.Distance(q[0], q[1]) + Geometry.Distance(q[2], q[3])) / 2;
        var s2 = (Geometry.Distance(q[1], q[2]) + Geometry.Distance(q[3], q[0])) / 2;
        var aspect = Math.Max(s1, s2) / Math.Max(Math.Min(s1, s2), 1);
        if (aspect > 6) shape *= 0.3;

        var size = Math.Clamp((areaRatio - 0.02) / 0.25, 0, 1) * (areaRatio > 0.93 ? 0.5 : 1);

        var totalScore = 0.30 * support + 0.12 * strengthScore + 0.16 * contrast * polarityBonus + 0.17 * paper + 0.10 * shape + 0.15 * size;
        totalScore *= 1 - Math.Clamp((border - 0.25) * 1.2, 0, 0.6);   // a little of the page off-frame is fine
        return new Breakdown(totalScore, support, strengthScore, contrast, paper, shape, size, border, brighterInside / (double)Math.Max(1, brighterInside + darkerInside), aspect);
    }

    /// <summary>
    /// The quadrilateral with one side moved outward, parallel, to the strongest consistent "lighter inside,
    /// darker outside" edge between it and well beyond (adjacent sides are extended along their lines).
    /// Empty when there is no such edge.
    /// </summary>
    public static List<Point2d[]> ExtendSide(Evidence e, Point2d[] q, int side)
    {
        var a = q[side];
        var b = q[(side + 1) % 4];
        var prev = q[(side + 3) % 4];
        var next = q[(side + 2) % 4];
        var len = Geometry.Distance(a, b);
        var found = new List<Point2d[]>();
        if (len < 20) return found;
        var cx = q.Average(p => p.X);
        var cy = q.Average(p => p.Y);
        var nx = (b.Y - a.Y) / len;
        var ny = -(b.X - a.X) / len;
        if (((a.X + b.X) / 2 - cx) * nx + ((a.Y + b.Y) / 2 - cy) * ny < 0) { nx = -nx; ny = -ny; }
        // How far out to look: up to 60% of the page's extent across this side.
        var depth = Math.Abs(((next.X + prev.X) / 2 - (a.X + b.X) / 2) * nx + ((next.Y + prev.Y) / 2 - (a.Y + b.Y) / 2) * ny);
        var maxOffset = (int)Math.Min(0.6 * depth, 0.4 * Math.Max(e.Width, e.Height));

        // Adjacent side directions (from the far corner towards this side's corner), to slide the corners along.
        var da = new Point2d(a.X - prev.X, a.Y - prev.Y);
        var db = new Point2d(b.X - next.X, b.Y - next.Y);
        var alongA = da.X * nx + da.Y * ny;
        var alongB = db.X * nx + db.Y * ny;
        if (alongA < 1e-3 || alongB < 1e-3) return found;

        double bestScore = 0;
        var bestOffset = 0;
        var outermost = 0;
        for (var o = 8; o <= maxOffset; o += 2)
        {
            var pa = new Point2d(a.X + da.X * o / alongA, a.Y + da.Y * o / alongA);
            var pb = new Point2d(b.X + db.X * o / alongB, b.Y + db.Y * o / alongB);
            int supported = 0, n = 0;
            double strength = 0;
            for (var i = 1; i < 32; i++)
            {
                var t = i / 32.0;
                var x = pa.X + (pb.X - pa.X) * t;
                var y = pa.Y + (pb.Y - pa.Y) * t;
                if (!e.Inside(x, y)) continue;
                n++;
                var d = -e.Along(x, y, nx, ny);   // brightness drop going outward
                if (d > 2.5) { supported++; strength += Math.Min(d, 20); }
            }
            if (n < 16) break;   // ran off the photo
            var support = supported / (double)n;
            if (support < 0.7) continue;
            var score = support * strength / n;
            if (score > bestScore) { bestScore = score; bestOffset = o; }
            outermost = o;
        }
        // Both the strongest edge and the outermost one (a sheet's own edge is the last paper edge going out;
        // lines of print inside it can be stronger). The ranker decides.
        foreach (var offset in new[] { bestOffset, outermost }.Distinct())
        {
            if (offset == 0) continue;
            var result = (Point2d[])q.Clone();
            result[side] = new Point2d(a.X + da.X * offset / alongA, a.Y + da.Y * offset / alongA);
            result[(side + 1) % 4] = new Point2d(b.X + db.X * offset / alongB, b.Y + db.Y * offset / alongB);
            if (IsConvex(result)) found.Add(result);
        }
        return found;
    }

    // ---------------------------------------------------------------- whole sheet

    /// <summary>
    /// The best-scoring quadrilateral is often one flat panel of a folded or creased sheet, because a crease is a
    /// strong straight edge too. A larger candidate wins instead when it contains the panel, scores nearly as well,
    /// keeps the panel's corners on its own outline (a crease runs from edge to edge of the sheet) and adds only
    /// paper-like area. A second sheet of a packet usually fails the corner test: it sticks out at an angle or is
    /// offset, so the top sheet's corners sit inside the union.
    /// </summary>
    public static Quad WholeSheet(Evidence e, IReadOnlyList<Quad> scored)
    {
        var best = scored[0];
        for (var round = 0; round < 3; round++)
        {
            var area = Math.Abs(Geometry.SignedArea(best.Corners));
            Quad? grown = null;
            double grownArea = 0;
            foreach (var c in scored)
            {
                if (c.Score < scored[0].Score - GrowMargin) continue;
                var ca = Math.Abs(Geometry.SignedArea(c.Corners));
                if (ca < 1.12 * area || ca <= grownArea) continue;
                if (Overlap(best.Corners, c.Corners) < 0.92 * area) continue;
                if (!CornersOnOutline(best.Corners, c.Corners) && !PrintInsidePage(e, best.Corners, c.Corners)) continue;
                if (!ExtraIsPaper(e, best.Corners, c.Corners)) continue;
                grown = c;
                grownArea = ca;
            }
            if (grown is null) break;
            best = grown with { Score = Math.Max(grown.Score, best.Score), Source = grown.Source + "+grown" };
        }
        return best;
    }

    internal static double GrowMargin = 0.10;
    internal static int BandPx = 4;

    internal static double Overlap(Point2d[] a, Point2d[] b)
    {
        var pa = a.Select(p => new Point2f((float)p.X, (float)p.Y)).ToArray();
        var pb = b.Select(p => new Point2f((float)p.X, (float)p.Y)).ToArray();
        return Cv2.IntersectConvexConvex(pa, pb, out _, true);
    }

    /// <summary>Every corner of the panel is on (or just inside) the larger outline's boundary.</summary>
    internal static bool CornersOnOutline(Point2d[] inner, Point2d[] outer)
    {
        var diag = Math.Max(Geometry.Distance(outer[0], outer[2]), Geometry.Distance(outer[1], outer[3]));
        var tolerance = 0.035 * diag;
        foreach (var p in inner)
            if (OutlineDistance(p, outer) > tolerance) return false;
        // Each side of the panel is either part of the sheet's outline or a fold well inside it. A side running
        // parallel just inside the outline is the edge of a top sheet with another sheet showing beneath it.
        var folds = 0;
        for (var i = 0; i < 4; i++)
        {
            var a = inner[i];
            var b = inner[(i + 1) % 4];
            var gap = Math.Max(OutlineDistance(new Point2d((a.X + b.X) / 2, (a.Y + b.Y) / 2), outer),
                Math.Max(OutlineDistance(new Point2d(a.X * 0.75 + b.X * 0.25, a.Y * 0.75 + b.Y * 0.25), outer),
                         OutlineDistance(new Point2d(a.X * 0.25 + b.X * 0.75, a.Y * 0.25 + b.Y * 0.75), outer)));
            if (gap <= OnOutline * diag) continue;
            if (gap < FoldDepth * diag) return false;
            folds++;
        }
        return folds >= 1;
    }

    /// <summary>
    /// The panel is a block of print (a photo, a table, a text column) on the page: just outside each of its sides
    /// that isn't on the larger outline there is blank margin, brighter than just inside. (Outside the top sheet of
    /// a packet is the next sheet down, in its shadow, so no brighter.)
    /// </summary>
    internal static bool PrintInsidePage(Evidence e, Point2d[] inner, Point2d[] outer)
    {
        var diag = Math.Max(Geometry.Distance(outer[0], outer[2]), Geometry.Distance(outer[1], outer[3]));
        var cx = inner.Average(p => p.X);
        var cy = inner.Average(p => p.Y);
        var interiorSides = 0;
        for (var i = 0; i < 4; i++)
        {
            var a = inner[i];
            var b = inner[(i + 1) % 4];
            var mid = new Point2d((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            if (OutlineDistance(mid, outer) <= OnOutline * diag) continue;
            interiorSides++;
            var len = Geometry.Distance(a, b);
            var tx = (b.X - a.X) / len;
            var ty = (b.Y - a.Y) / len;
            var nx = ty;
            var ny = -tx;
            if ((mid.X - cx) * nx + (mid.Y - cy) * ny < 0) { nx = -nx; ny = -ny; }
            double inside = 0, outside = 0;
            var n = 0;
            for (var k = 1; k < 24; k++)
            {
                var t = k / 24.0;
                var x = a.X + (b.X - a.X) * t;
                var y = a.Y + (b.Y - a.Y) * t;
                for (var d = 3; d <= 12; d += 3)
                {
                    inside += e.GrayAt(x - nx * d, y - ny * d);
                    outside += e.GrayAt(x + nx * d, y + ny * d);
                    n++;
                }
            }
            if (outside / n < inside / n + MarginLift) return false;
        }
        return interiorSides > 0;
    }

    internal static double MarginLift = 4;

    internal static double OnOutline = 0.025, FoldDepth = 0.10;

    static double OutlineDistance(Point2d p, Point2d[] outline)
    {
        var nearest = double.MaxValue;
        for (var i = 0; i < outline.Length; i++) nearest = Math.Min(nearest, SegmentDistance(p, outline[i], outline[(i + 1) % outline.Length]));
        return nearest;
    }

    static double SegmentDistance(Point2d p, Point2d a, Point2d b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / (dx * dx + dy * dy + 1e-9), 0, 1);
        return Geometry.Distance(p, new Point2d(a.X + dx * t, a.Y + dy * t));
    }

    /// <summary>
    /// The area the larger outline adds is paper too. Judged on its margin, the band just inside the larger
    /// outline where it runs beyond the panel (print in the middle of the added area is fine): not much more
    /// colorful or darker than the panel's own margin, and more like paper than like what the paper lies on.
    /// </summary>
    internal static bool ExtraIsPaper(Evidence e, Point2d[] inner, Point2d[] outer)
    {
        Point[] Small(Point2d[] q) => q.Select(p => new Point((int)Math.Round(p.X / 4), (int)Math.Round(p.Y / 4))).ToArray();
        var size = e.SmallGray.Size();
        using var innerMask = new Mat(size, MatType.CV_8UC1, Scalar.All(0));
        using var outerMask = new Mat(size, MatType.CV_8UC1, Scalar.All(0));
        Cv2.FillConvexPoly(innerMask, Small(inner), Scalar.All(255));
        Cv2.FillConvexPoly(outerMask, Small(outer), Scalar.All(255));
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * BandPx + 1, 2 * BandPx + 1));
        using var innerCore = new Mat();
        using var outerCore = new Mat();
        using var outerGrown = new Mat();
        Cv2.Erode(innerMask, innerCore, kernel);
        Cv2.Erode(outerMask, outerCore, kernel);
        Cv2.Dilate(outerMask, outerGrown, kernel);
        using var innerBand = new Mat();
        using var outerBand = new Mat();
        using var outsideBand = new Mat();
        Cv2.Subtract(innerMask, innerCore, innerBand);
        Cv2.Subtract(outerMask, outerCore, outerBand);
        Cv2.Subtract(outerBand, innerMask, outerBand);           // only where the outline runs beyond the panel
        Cv2.Subtract(outerGrown, outerMask, outsideBand);
        if (Cv2.CountNonZero(outerBand) < 20 || Cv2.CountNonZero(innerBand) < 20) return false;

        var grayIn = Cv2.Mean(e.SmallGray, innerBand).Val0;
        var satIn = Cv2.Mean(e.SmallSaturation, innerBand).Val0;
        var grayExtra = Cv2.Mean(e.SmallGray, outerBand).Val0;
        var satExtra = Cv2.Mean(e.SmallSaturation, outerBand).Val0;
        if (satExtra > satIn + 25) return false;
        if (grayExtra < 0.6 * grayIn) return false;
        if (Cv2.CountNonZero(outsideBand) > 20)
        {
            var grayOut = Cv2.Mean(e.SmallGray, outsideBand).Val0;
            var satOut = Cv2.Mean(e.SmallSaturation, outsideBand).Val0;
            var toPaper = Math.Abs(grayExtra - grayIn) + Math.Abs(satExtra - satIn);
            var toOutside = Math.Abs(grayExtra - grayOut) + Math.Abs(satExtra - satOut);
            if (toOutside < toPaper) return false;

            // And little of the added area looks like the surface around it (the table between two sheets).
            using var extra = new Mat();
            Cv2.Subtract(outerMask, innerMask, extra);
            Cv2.Erode(extra, extra, kernel);
            var extraCount = Cv2.CountNonZero(extra);
            if (extraCount > 20)
            {
                using var grayDiff = new Mat();
                using var satDiff = new Mat();
                Cv2.Absdiff(e.SmallGray, new Scalar(grayOut), grayDiff);
                Cv2.Absdiff(e.SmallSaturation, new Scalar(satOut), satDiff);
                using var distance = new Mat();
                Cv2.Add(grayDiff, satDiff, distance);
                using var like = new Mat();
                Cv2.Threshold(distance, like, BackgroundTolerance, 255, ThresholdTypes.BinaryInv);
                Cv2.BitwiseAnd(like, extra, like);
                if (Cv2.CountNonZero(like) > MaxBackground * extraCount) return false;
            }
        }
        return true;
    }

    internal static double BackgroundTolerance = 18, MaxBackground = 0.2;

    static double CornerAngle(Point2d previous, Point2d corner, Point2d next)
    {
        double ax = previous.X - corner.X, ay = previous.Y - corner.Y;
        double bx = next.X - corner.X, by = next.Y - corner.Y;
        var cos = (ax * bx + ay * by) / (Math.Sqrt(ax * ax + ay * ay) * Math.Sqrt(bx * bx + by * by) + 1e-9);
        return Math.Acos(Math.Clamp(cos, -1, 1)) * 180 / Math.PI;
    }

    /// <summary>
    /// Straight-line corners for a region outline: robust line fits to the middle of each side, intersected.
    /// Folded, rounded or torn corners then don't pull the geometry.
    /// </summary>
    public static Point2d[]? LineCorners(IReadOnlyList<Point2d> contour, Point2d[] roughCorners)
    {
        var n = contour.Count;
        var idx = roughCorners.Select(c => NearestIndex(contour, c)).ToArray();
        var fitted = new (Point2d P, Point2d D)?[4];
        for (var s = 0; s < 4; s++)
        {
            var from = idx[s];
            var to = idx[(s + 1) % 4];
            var count = (to - from + n) % n;
            if (count < 8) return null;
            var pts = new List<Point2f>();
            for (var k = (int)(count * 0.2); k <= (int)(count * 0.8); k++)
            {
                var p = contour[(from + k) % n];
                pts.Add(new Point2f((float)p.X, (float)p.Y));
            }
            if (pts.Count < 5) return null;
            var line = Cv2.FitLine(pts, DistanceTypes.Huber, 0, 0.01, 0.01);
            fitted[s] = (new Point2d(line.X1, line.Y1), new Point2d(line.Vx, line.Vy));
        }
        var corners = new Point2d[4];
        for (var i = 0; i < 4; i++)
        {
            var a = fitted[(i + 3) % 4]!.Value;
            var b = fitted[i]!.Value;
            var den = a.D.X * b.D.Y - a.D.Y * b.D.X;
            if (Math.Abs(den) < 1e-6) return null;
            var t = ((b.P.X - a.P.X) * b.D.Y - (b.P.Y - a.P.Y) * b.D.X) / den;
            corners[i] = new Point2d(a.P.X + a.D.X * t, a.P.Y + a.D.Y * t);
            // A virtual corner far from where the paper's corner is means the side fits were bad; keep the rough one.
            if (Geometry.Distance(corners[i], roughCorners[i]) > 0.12 * Geometry.Distance(roughCorners[0], roughCorners[2])) corners[i] = roughCorners[i];
        }
        return corners;
    }

    static int NearestIndex(IReadOnlyList<Point2d> points, Point2d target)
    {
        var best = 0;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < points.Count; i++)
        {
            var d = Geometry.Distance(points[i], target);
            if (d < bestDistance) { bestDistance = d; best = i; }
        }
        return best;
    }
}
