using OpenCvSharp;

namespace Taildrop.Core.Scanning;

/// <summary>
/// Finds the page in a photo: the whole outline (not just four corner points), so curled receipts,
/// folded sheets and dog-eared corners are captured completely. Several independent ways of
/// segmenting the paper (strong edges, bright pixels, neutral-colored pixels) each propose an outline;
/// the best-scoring plausible one wins and is refined with GrabCut.
/// </summary>
public static class DocumentDetector
{
    const int WorkEdge = 1000;
    const double MinConfidence = 0.58;
    /// <summary>A narrow receipt in a landscape frame can be only a few percent of the photo.</summary>
    const double MinAreaRatio = 0.025;
    /// <summary>Below this a "page" is a guess; the full frame is a more honest starting point for manual adjustment.</summary>
    const double MinAcceptedScore = 0.4;

    public static ScanOutline Detect(Mat image)
    {
        var scale = Math.Min(1.0, WorkEdge / (double)Math.Max(image.Width, image.Height));
        using var small = new Mat();
        if (scale < 1) Cv2.Resize(image, small, new Size(), scale, scale, InterpolationFlags.Area);
        else image.CopyTo(small);

        using var context = new Analysis(small);
        var best = FindBestCandidate(context);
        if (best is null) return ScanOutline.FullFrame();

        var refined = Refine(context, best);
        if (refined is not null) best = refined;

        var outline = BuildOutline(best.Contour, small.Size());
        if (outline is null) return ScanOutline.FullFrame();
        outline.Confident = best.Score >= MinConfidence;
        return outline;
    }

    // ---- candidate generation -------------------------------------------------------------------------------

    sealed class Candidate
    {
        public required Point[] Contour { get; init; }
        public double Score { get; init; }
        public string Source { get; init; } = "";
    }

    /// <summary>Per-photo data shared by all the candidate generators and the scorer.</summary>
    sealed class Analysis : IDisposable
    {
        public Mat Bgr { get; }
        public Mat Gray { get; } = new();
        public Mat Blurred { get; } = new();
        /// <summary>Gradient magnitude in gray levels per pixel (a sharp step of N levels reads about N), dilated a little.</summary>
        public Mat Gradient { get; } = new();
        public Size Size => Bgr.Size();
        public double FrameArea => Bgr.Width * (double)Bgr.Height;

        public Analysis(Mat bgr)
        {
            Bgr = bgr;
            Cv2.CvtColor(bgr, Gray, ColorConversionCodes.BGR2GRAY);
            Cv2.GaussianBlur(Gray, Blurred, new Size(5, 5), 0);
            using var gx = new Mat();
            using var gy = new Mat();
            Cv2.Sobel(Blurred, gx, MatType.CV_32F, 1, 0, 3);
            Cv2.Sobel(Blurred, gy, MatType.CV_32F, 0, 1, 3);
            using var magnitude = new Mat();
            Cv2.Magnitude(gx, gy, magnitude);
            using var scaled = new Mat();
            magnitude.ConvertTo(scaled, MatType.CV_32F, 0.25);
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
            Cv2.Dilate(scaled, Gradient, kernel);
        }

        public void Dispose()
        {
            Gray.Dispose();
            Blurred.Dispose();
            Gradient.Dispose();
        }
    }

    static Candidate? FindBestCandidate(Analysis a)
    {
        var candidates = new List<Candidate>();
        AddFromMask(a, EdgeMask(a), "edges", candidates, useHull: false);
        AddFromMask(a, WeakEdgeMask(a), "weak-edges", candidates, useHull: false);
        AddFromMask(a, BrightMask(a), "bright", candidates, useHull: false);
        AddFromMask(a, NeutralMask(a), "neutral", candidates, useHull: false);
        return candidates.Where(c => c.Score >= MinAcceptedScore).OrderByDescending(c => c.Score).FirstOrDefault();
    }

    /// <summary>Closed regions bounded by strong edges, after erasing thin dark print so text does not break them up.</summary>
    static Mat EdgeMask(Analysis a)
    {
        using var closeKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(9, 9));
        using var cleaned = new Mat();
        Cv2.MorphologyEx(a.Blurred, cleaned, MorphTypes.Close, closeKernel);
        using var scratch = new Mat();
        var otsu = Cv2.Threshold(cleaned, scratch, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        using var edges = new Mat();
        Cv2.Canny(cleaned, edges, Math.Max(10, otsu * 0.33), Math.Max(30, otsu * 0.9));
        using var join = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(7, 7));
        var closed = new Mat();
        Cv2.MorphologyEx(edges, closed, MorphTypes.Close, join);
        return FilledRegions(closed);
    }

    /// <summary>
    /// For paper on a similar-colored surface (white on white): lighting-flattened, text-erased image with very
    /// low edge thresholds. The scorer discounts these candidates for their weak boundary but keeps them if the
    /// shape is unmistakably a page.
    /// </summary>
    static Mat WeakEdgeMask(Analysis a)
    {
        using var closeKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(9, 9));
        using var cleaned = new Mat();
        Cv2.MorphologyEx(a.Gray, cleaned, MorphTypes.Close, closeKernel);
        using var smooth = new Mat();
        Cv2.GaussianBlur(cleaned, smooth, new Size(0, 0), 2.0);
        using var edges = new Mat();
        Cv2.Canny(smooth, edges, 6, 16);
        using var join = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(9, 9));
        var closed = new Mat();
        Cv2.MorphologyEx(edges, closed, MorphTypes.Close, join);
        return FilledRegions(closed);
    }

    /// <summary>Paper is usually brighter than what it lies on.</summary>
    static Mat BrightMask(Analysis a)
    {
        using var scratch = new Mat();
        var otsu = Cv2.Threshold(a.Blurred, scratch, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        var mask = new Mat();
        Cv2.Threshold(a.Blurred, mask, otsu, 255, ThresholdTypes.Binary);
        return Tidy(mask);
    }

    /// <summary>White paper is nearly colorless; wood, cloth and most table tops are not.</summary>
    static Mat NeutralMask(Analysis a)
    {
        using var lab = new Mat();
        Cv2.CvtColor(a.Bgr, lab, ColorConversionCodes.BGR2Lab);
        var channels = Cv2.Split(lab);
        try
        {
            using var da = new Mat();
            using var db = new Mat();
            channels[1].ConvertTo(da, MatType.CV_32F, 1, -128);
            channels[2].ConvertTo(db, MatType.CV_32F, 1, -128);
            using var chroma = new Mat();
            Cv2.Magnitude(da, db, chroma);
            using var chroma8 = new Mat();
            chroma.ConvertTo(chroma8, MatType.CV_8U, 4); // 0-63 chroma units -> 0-252
            Cv2.GaussianBlur(chroma8, chroma8, new Size(7, 7), 0);
            using var scratch = new Mat();
            var otsu = Cv2.Threshold(chroma8, scratch, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
            var mask = new Mat();
            Cv2.Threshold(chroma8, mask, otsu, 255, ThresholdTypes.BinaryInv);
            return Tidy(mask);
        }
        finally
        {
            foreach (var c in channels) c.Dispose();
        }
    }

    /// <summary>Closes print and creases inside the page and removes specks outside it.</summary>
    static Mat Tidy(Mat mask)
    {
        using var closeKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(15, 15));
        using var openKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(9, 9));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, closeKernel);
        Cv2.MorphologyEx(mask, mask, MorphTypes.Open, openKernel);
        return mask;
    }

    /// <summary>Fills the area enclosed by contours so a ring of edges becomes a solid region.</summary>
    static Mat FilledRegions(Mat edges)
    {
        Cv2.FindContours(edges, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var filled = new Mat(edges.Size(), MatType.CV_8UC1, Scalar.All(0));
        Cv2.DrawContours(filled, contours, -1, Scalar.All(255), -1);
        return filled;
    }

    static void AddFromMask(Analysis a, Mat mask, string source, List<Candidate> candidates, bool useHull)
    {
        using (mask)
        {
            Cv2.FindContours(mask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxNone);
            foreach (var contour in contours.OrderByDescending(c => Cv2.ContourArea(c)).Take(3))
            {
                if (Cv2.ContourArea(contour) < MinAreaRatio * a.FrameArea) continue;
                var polygon = useHull ? Cv2.ConvexHull(contour) : contour;
                var score = Score(a, polygon);
                candidates.Add(new Candidate { Contour = polygon, Score = score, Source = source });
            }
        }
    }

    // ---- scoring --------------------------------------------------------------------------------------------

    /// <summary>
    /// How much this outline looks like a sheet of paper in a photo, 0-1: a clear boundary along the whole
    /// contour, close to a rectangle, sensible corner angles, a substantial but not frame-filling size, and
    /// not hugging the picture border (that would be the backdrop, not the page).
    /// </summary>
    static ScoreBreakdown ScoreDetails(Analysis a, Point[] polygon)
    {
        var area = Cv2.ContourArea(polygon);
        var areaRatio = area / a.FrameArea;
        if (areaRatio < MinAreaRatio || areaRatio > 0.985) return default;

        var hull = Cv2.ConvexHull(polygon);
        var hullArea = Cv2.ContourArea(hull);
        var solidity = hullArea > 0 ? area / hullArea : 0;

        var quad = QuadFit.Fit(hull);
        if (quad is null) return default;
        var quadArea = Math.Abs(Geometry.SignedArea(quad.Select(p => new Point2d(p.X, p.Y)).ToArray()));
        if (quadArea < 1) return default;

        var rectangularity = Math.Clamp(1 - Math.Abs(area / quadArea - 1) * 3, 0, 1);

        var angleScore = 1.0;
        for (var i = 0; i < 4; i++)
        {
            var angle = CornerAngle(quad[(i + 3) % 4], quad[i], quad[(i + 1) % 4]);
            var off = Math.Max(0, Math.Max(55 - angle, angle - 125));
            angleScore = Math.Min(angleScore, Math.Clamp(1 - off / 25, 0, 1));
        }

        // Boundary strength and border contact, sampled along the contour.
        double support = 0;
        var onBorder = 0;
        var step = Math.Max(1, polygon.Length / 400);
        var samples = 0;
        for (var i = 0; i < polygon.Length; i += step)
        {
            var p = polygon[i];
            support += a.Gradient.At<float>(Math.Clamp(p.Y, 0, a.Size.Height - 1), Math.Clamp(p.X, 0, a.Size.Width - 1));
            if (p.X <= 3 || p.Y <= 3 || p.X >= a.Size.Width - 4 || p.Y >= a.Size.Height - 4) onBorder++;
            samples++;
        }
        var edgeSupport = Math.Clamp(support / samples / 22.0, 0, 1);
        var borderShare = onBorder / (double)samples;

        var sizeScore = Math.Clamp((areaRatio - MinAreaRatio) / 0.3, 0, 1) * (areaRatio > 0.93 ? 0.4 : 1);
        var solidityScore = Math.Clamp((solidity - 0.7) / 0.22, 0, 1);

        var m = Cv2.Moments(polygon);
        double centerScore = 0;
        if (m.M00 > 0)
        {
            var dx = (m.M10 / m.M00 - a.Size.Width / 2.0) / a.Size.Width;
            var dy = (m.M01 / m.M00 - a.Size.Height / 2.0) / a.Size.Height;
            centerScore = Math.Clamp(1 - 1.4 * Math.Sqrt(dx * dx + dy * dy), 0, 1);
        }

        var total = 0.30 * edgeSupport + 0.20 * rectangularity + 0.15 * angleScore + 0.15 * sizeScore + 0.10 * solidityScore + 0.10 * centerScore;
        total *= 1 - Math.Min(0.7, borderShare * 1.5);
        return new ScoreBreakdown(total, edgeSupport, rectangularity, angleScore, sizeScore, solidityScore, centerScore, borderShare);
    }

    static double Score(Analysis a, Point[] polygon) => ScoreDetails(a, polygon).Total;

    readonly record struct ScoreBreakdown(double Total, double Edge, double Rect, double Angle, double Size, double Solidity, double Center, double Border);

    /// <summary>For diagnostics in tests.</summary>
    internal static string Describe(Mat image)
    {
        var scale = Math.Min(1.0, WorkEdge / (double)Math.Max(image.Width, image.Height));
        using var small = new Mat();
        Cv2.Resize(image, small, new Size(), scale, scale, InterpolationFlags.Area);
        using var a = new Analysis(small);
        var lines = new List<string>();
        foreach (var (name, mask) in new[] { ("edges", EdgeMask(a)), ("weak", WeakEdgeMask(a)), ("bright", BrightMask(a)), ("neutral", NeutralMask(a)) })
        {
            using (mask)
            {
                Cv2.FindContours(mask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxNone);
                foreach (var c in contours.OrderByDescending(c => Cv2.ContourArea(c)).Take(2))
                {
                    var s = ScoreDetails(a, c);
                    lines.Add($"{name}: area {Cv2.ContourArea(c) / a.FrameArea:P0} score {s.Total:F2} edge {s.Edge:F2} rect {s.Rect:F2} angle {s.Angle:F2} size {s.Size:F2} sol {s.Solidity:F2} ctr {s.Center:F2} border {s.Border:F2}");
                }
            }
        }
        return string.Join("\n", lines);
    }

    static double CornerAngle(Point previous, Point corner, Point next)
    {
        double ax = previous.X - corner.X, ay = previous.Y - corner.Y;
        double bx = next.X - corner.X, by = next.Y - corner.Y;
        var cos = (ax * bx + ay * by) / (Math.Sqrt(ax * ax + ay * ay) * Math.Sqrt(bx * bx + by * by) + 1e-9);
        return Math.Acos(Math.Clamp(cos, -1, 1)) * 180 / Math.PI;
    }

    // ---- refinement -----------------------------------------------------------------------------------------

    /// <summary>
    /// GrabCut inside a band around the candidate: snaps the boundary onto the real paper edge (including
    /// curls and dog-ears) where a plain threshold leaves ragged or leaky outlines. Kept only if it still
    /// looks like the same page and scores at least as well.
    /// </summary>
    static Candidate? Refine(Analysis a, Candidate candidate)
    {
        var shrink = Math.Min(1.0, 640.0 / Math.Max(a.Size.Width, a.Size.Height));
        using var image = new Mat();
        if (shrink < 1) Cv2.Resize(a.Bgr, image, new Size(), shrink, shrink, InterpolationFlags.Area);
        else a.Bgr.CopyTo(image);

        var polygon = candidate.Contour.Select(p => new Point((int)Math.Round(p.X * shrink), (int)Math.Round(p.Y * shrink))).ToArray();
        using var inside = new Mat(image.Size(), MatType.CV_8UC1, Scalar.All(0));
        Cv2.FillPoly(inside, new[] { polygon }, Scalar.All(255));

        var band = Math.Max(4, (int)(Math.Min(image.Width, image.Height) * 0.045));
        using var coreKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(band * 2 + 1, band * 2 + 1));
        using var core = new Mat();
        using var outer = new Mat();
        Cv2.Erode(inside, core, coreKernel);
        Cv2.Dilate(inside, outer, coreKernel);

        // 0 = sure background, 1 = sure foreground, 2 = probably background, 3 = probably foreground.
        using var mask = new Mat(image.Size(), MatType.CV_8UC1, Scalar.All(0));
        mask.SetTo(Scalar.All(2), outer);
        mask.SetTo(Scalar.All(3), inside);
        mask.SetTo(Scalar.All(1), core);

        try
        {
            using var background = new Mat();
            using var foreground = new Mat();
            Cv2.GrabCut(image, mask, default, background, foreground, 4, GrabCutModes.InitWithMask);
        }
        catch (OpenCVException)
        {
            return null;
        }

        using var result = new Mat();
        using var fg = new Mat();
        using var pfg = new Mat();
        Cv2.Compare(mask, Scalar.All(1), fg, CmpTypes.EQ);
        Cv2.Compare(mask, Scalar.All(3), pfg, CmpTypes.EQ);
        Cv2.BitwiseOr(fg, pfg, result);
        using var smooth = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(5, 5));
        Cv2.MorphologyEx(result, result, MorphTypes.Open, smooth);
        Cv2.MorphologyEx(result, result, MorphTypes.Close, smooth);

        Cv2.FindContours(result, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxNone);
        var largest = contours.OrderByDescending(c => Cv2.ContourArea(c)).FirstOrDefault();
        if (largest is null) return null;

        var refinedArea = Cv2.ContourArea(largest);
        var originalArea = Cv2.ContourArea(polygon);
        if (originalArea < 1 || refinedArea / originalArea is < 0.8 or > 1.25) return null;

        var contour = largest.Select(p => new Point((int)Math.Round(p.X / shrink), (int)Math.Round(p.Y / shrink))).ToArray();
        var score = Score(a, contour);
        return score >= candidate.Score - 0.02 ? new Candidate { Contour = contour, Score = score, Source = candidate.Source + "+grabcut" } : null;
    }

    // ---- outline construction -------------------------------------------------------------------------------

    /// <summary>Corners from the contour's hull, then the contour itself split into four edge runs between them.</summary>
    static ScanOutline? BuildOutline(Point[] contour, Size size)
    {
        var points = contour.Select(p => new Point2d(p.X, p.Y)).ToList();
        if (points.Count < 8) return null;
        if (Geometry.SignedArea(points) < 0) points.Reverse(); // clockwise on screen

        var hull = Cv2.ConvexHull(points.Select(p => new Point((int)p.X, (int)p.Y)).ToArray());
        var quad = QuadFit.Fit(hull);
        if (quad is null) return null;

        var centre = new Point2d(points.Average(p => p.X), points.Average(p => p.Y));
        var corners = SnapOutward(quad.Select(p => new Point2d(p.X, p.Y)).ToArray(), points, centre);
        corners = OrderCorners(corners, centre);

        // Contour index nearest to each corner (clockwise order TL, TR, BR, BL).
        var indices = corners.Select(c => NearestIndex(points, c)).ToArray();
        for (var i = 0; i < 4; i++) if (indices[i] == indices[(i + 1) % 4]) return null;

        Point2d[] Run(int from, int to)
        {
            var run = new List<Point2d>();
            for (var i = from; ; i = (i + 1) % points.Count)
            {
                run.Add(points[i]);
                if (i == to) break;
            }
            return run.ToArray();
        }

        var top = Prepare(Run(indices[0], indices[1]), corners[0], corners[1]);
        var right = Prepare(Run(indices[1], indices[2]), corners[1], corners[2]);
        var bottom = Prepare(Run(indices[2], indices[3]), corners[2], corners[3]).Reverse().ToArray(); // BR->BL becomes BL->BR
        var left = Prepare(Run(indices[3], indices[0]), corners[3], corners[0]).Reverse().ToArray();    // BL->TL becomes TL->BL

        double[] N(Point2d p) => new[] { p.X / size.Width, p.Y / size.Height };
        return new ScanOutline
        {
            Corners = corners.Select(N).ToArray(),
            Top = top.Select(N).ToArray(),
            Right = right.Select(N).ToArray(),
            Bottom = bottom.Select(N).ToArray(),
            Left = left.Select(N).ToArray()
        };
    }

    /// <summary>Resamples an edge run, pins its ends to the corners, smooths it, and straightens it when it is only noise.</summary>
    static Point2d[] Prepare(Point2d[] run, Point2d start, Point2d end)
    {
        var samples = Geometry.ResampleByArcLength(run, ScanOutline.EdgeSamples);
        samples[0] = start;
        samples[^1] = end;
        Geometry.SmoothInPlace(samples, 3);

        // Within ~0.7% of the edge length of a straight line is noise from the segmentation, not a bend.
        var chord = Geometry.Distance(start, end);
        if (Geometry.MaxDeviationFromChord(samples) < 0.007 * chord)
        {
            for (var i = 0; i < samples.Length; i++)
            {
                var t = i / (double)(samples.Length - 1);
                samples[i] = new Point2d(start.X + (end.X - start.X) * t, start.Y + (end.Y - start.Y) * t);
            }
        }
        return samples;
    }

    /// <summary>Moves each corner to the contour point that sticks out furthest in its direction, so rounded or noisy corners lose nothing.</summary>
    static Point2d[] SnapOutward(Point2d[] corners, List<Point2d> contour, Point2d centre)
    {
        var diagonal = Math.Sqrt(
            Math.Pow(corners.Max(c => c.X) - corners.Min(c => c.X), 2) + Math.Pow(corners.Max(c => c.Y) - corners.Min(c => c.Y), 2));
        var radius = 0.08 * diagonal;
        var snapped = new Point2d[4];
        for (var i = 0; i < 4; i++)
        {
            var direction = new Point2d(corners[i].X - centre.X, corners[i].Y - centre.Y);
            var length = Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
            direction = new Point2d(direction.X / Math.Max(length, 1e-9), direction.Y / Math.Max(length, 1e-9));
            var best = corners[i];
            var bestReach = double.NegativeInfinity;
            foreach (var p in contour)
            {
                if (Geometry.Distance(p, corners[i]) > radius) continue;
                var reach = (p.X - centre.X) * direction.X + (p.Y - centre.Y) * direction.Y;
                if (reach > bestReach) { bestReach = reach; best = p; }
            }
            snapped[i] = best;
        }
        return snapped;
    }

    /// <summary>
    /// Orders four corners clockwise as TL, TR, BR, BL. "Top" is the edge that points most to the right when
    /// walking clockwise, i.e. the page is upright as the photo shows it.
    /// </summary>
    static Point2d[] OrderCorners(Point2d[] corners, Point2d centre)
    {
        var clockwise = corners.OrderBy(c => Math.Atan2(c.Y - centre.Y, c.X - centre.X)).ToArray();
        var bestStart = 0;
        var bestScore = double.NegativeInfinity;
        for (var i = 0; i < 4; i++)
        {
            var a = clockwise[i];
            var b = clockwise[(i + 1) % 4];
            var length = Geometry.Distance(a, b);
            var score = length < 1e-9 ? double.NegativeInfinity : (b.X - a.X) / length;
            if (score > bestScore) { bestScore = score; bestStart = i; }
        }
        return Enumerable.Range(0, 4).Select(i => clockwise[(bestStart + i) % 4]).ToArray();
    }

    static int NearestIndex(List<Point2d> points, Point2d target)
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

/// <summary>Best four-corner approximation of a convex polygon.</summary>
static class QuadFit
{
    public static Point[]? Fit(Point[] hull)
    {
        if (hull.Length < 4) return null;
        var perimeter = Cv2.ArcLength(hull, true);

        // Simplify until only a handful of strong corners remain.
        Point[] simplified = hull;
        foreach (var epsilon in new[] { 0.01, 0.015, 0.022, 0.03, 0.045, 0.065 })
        {
            simplified = Cv2.ApproxPolyDP(hull, epsilon * perimeter, true);
            if (simplified.Length <= 8) break;
        }
        if (simplified.Length < 4) return MinAreaCorners(hull);
        if (simplified.Length == 4) return simplified;
        if (simplified.Length > 14) simplified = Cv2.ApproxPolyDP(hull, 0.09 * perimeter, true);
        if (simplified.Length < 4 || simplified.Length > 14) return MinAreaCorners(hull);

        // Of the remaining corners keep the four that enclose the most area.
        var n = simplified.Length;
        double bestArea = -1;
        Point[]? best = null;
        for (var a = 0; a < n - 3; a++)
            for (var b = a + 1; b < n - 2; b++)
                for (var c = b + 1; c < n - 1; c++)
                    for (var d = c + 1; d < n; d++)
                    {
                        var candidate = new[] { simplified[a], simplified[b], simplified[c], simplified[d] };
                        var area = Math.Abs(Geometry.SignedArea(candidate.Select(p => new Point2d(p.X, p.Y)).ToArray()));
                        if (area > bestArea) { bestArea = area; best = candidate; }
                    }
        return best;
    }

    static Point[] MinAreaCorners(Point[] hull)
    {
        var rect = Cv2.MinAreaRect(hull.Select(p => new Point2f(p.X, p.Y)));
        return rect.Points().Select(p => new Point((int)Math.Round(p.X), (int)Math.Round(p.Y))).ToArray();
    }
}
