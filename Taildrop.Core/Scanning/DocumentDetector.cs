using OpenCvSharp;

namespace Taildrop.Core.Scanning;

/// <summary>
/// Finds the page in a photo. Page quadrilaterals are proposed from straight lines in the photo, from paper-like
/// regions (strong edges, weak edges, bright pixels, neutral-colored pixels) and from the best of those with a side
/// moved out to a fainter edge, all scored by <see cref="QuadFinder"/>. <see cref="PageRanker"/>, trained on photos
/// with known pages, picks the whole sheet among them (not a panel of a fold, a block of print, or a packet's other
/// sheets); its real, possibly curved, edges are then traced by <see cref="EdgeTracer"/>.
/// (A GrabCut refinement was tried and removed: it was non-deterministic and clipped colored corners.)
/// </summary>
public static class DocumentDetector
{
    internal const int WorkEdge = 1000;
    const double MinConfidence = 0.55;
    /// <summary>A narrow receipt in a landscape frame can be only a few percent of the photo.</summary>
    const double MinAreaRatio = 0.025;
    /// <summary>Below this a "page" is a guess; the full frame is a more honest starting point for manual adjustment.</summary>
    const double MinAcceptedScore = 0.35;
    /// <summary>With the trained ranker the score is the predicted overlap with the real page.</summary>
    internal static double MinPredictedOverlap = 0.3, ConfidentOverlap = 0.6;

    public static ScanOutline Detect(Mat image)
    {
        var scale = Math.Min(1.0, WorkEdge / (double)Math.Max(image.Width, image.Height));
        using var small = new Mat();
        if (scale < 1) Cv2.Resize(image, small, new Size(), scale, scale, InterpolationFlags.Area);
        else image.CopyTo(small);

        var best = FindPage(small);
        var ranked = best?.Source.EndsWith("ranked") == true;
        if (best is null || best.Score < (ranked ? MinPredictedOverlap : MinAcceptedScore)) return ScanOutline.FullFrame();
        var corners = best.Corners.Select(c => new Point2d(c.X / scale, c.Y / scale)).ToArray();
        return EdgeTracer.Trace(image, corners, best.Score >= (ranked ? ConfidentOverlap : MinConfidence));
    }

    /// <summary>
    /// Page corners in a live camera frame (normalized, TL TR BR BL), or null when no page is in view. The same
    /// detector with a shorter list of candidates and straight edges only, so the phone can draw the outline several
    /// times a second; the frame that is finally captured gets the full <see cref="Detect"/>.
    /// </summary>
    public static (double[][] Corners, bool Confident)? DetectQuick(Mat frame)
    {
        var scale = Math.Min(1.0, QuickWorkEdge / (double)Math.Max(frame.Width, frame.Height));
        using var small = new Mat();
        if (scale < 1) Cv2.Resize(frame, small, new Size(), scale, scale, InterpolationFlags.Area);
        else frame.CopyTo(small);

        var best = FindPage(small, quick: true);
        var ranked = best?.Source.EndsWith("ranked") == true;
        if (best is null || best.Score < (ranked ? MinPredictedOverlap : MinAcceptedScore)) return null;
        var corners = best.Corners.Select(c => new[] { c.X / small.Width, c.Y / small.Height }).ToArray();
        return (corners, best.Score >= (ranked ? ConfidentOverlap : MinConfidence));
    }

    internal const int QuickWorkEdge = 640;

    /// <summary>Best page quadrilateral (working-image pixels, TL TR BR BL) among all hypotheses.</summary>
    internal static QuadFinder.Quad? FindPage(Mat small, bool quick = false)
    {
        using var evidence = new QuadFinder.Evidence(small);
        var scored = Candidates(small, evidence, quick);
        if (scored.Count == 0) return null;
        if (!UseRanker || !PageRanker.Available) return QuadFinder.WholeSheet(evidence, scored);
        var features = PageRanker.Features(evidence, scored);
        QuadFinder.Quad? best = null;
        for (var i = 0; i < scored.Count; i++)
        {
            var predicted = PageRanker.Predict(features[i]);
            if (best is null || predicted > best.Score) best = scored[i] with { Score = predicted, Source = scored[i].Source + "+ranked" };
        }
        return best;
    }

    /// <summary>For the benchmark: pick with the hand-tuned rules instead of the trained ranker.</summary>
    internal static bool UseRanker = true;
    internal static int Shortlist = 40;
    /// <summary>Live preview: fewer line hypotheses get a full score and fewer outlines are re-tried with a side moved out.</summary>
    internal static int QuickShortlist = 16, QuickExtendTop = 2;

    /// <summary>All page hypotheses worth a full score, best first.</summary>
    internal static List<QuadFinder.Quad> Candidates(Mat small, QuadFinder.Evidence evidence, bool quick = false)
    {
        var shortlistSize = quick ? QuickShortlist : Shortlist;
        var extendTop = quick ? QuickExtendTop : ExtendTop;
        using var context = new Analysis(small);

        var hypotheses = new List<(Point2d[] Corners, string Source)>();
        foreach (var (name, mask) in new[] { ("edges", EdgeMask(context)), ("weak-edges", WeakEdgeMask(context)), ("bright", BrightMask(context)), ("neutral", NeutralMask(context)) })
        {
            using (mask)
            {
                Cv2.FindContours(mask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxNone);
                foreach (var contour in contours.OrderByDescending(c => Cv2.ContourArea(c)).Take(3))
                {
                    if (Cv2.ContourArea(contour) < MinAreaRatio * context.FrameArea) continue;
                    var quad = RegionQuad(contour);
                    if (quad is not null) hypotheses.Add((quad, name));
                }
            }
        }
        foreach (var quad in QuadFinder.LineHypotheses(small, evidence)) hypotheses.Add((quad, "lines"));
        if (hypotheses.Count == 0) return new();

        // Cheap perimeter score for everything, full score (with the inside of the page) for the best few.
        // Region outlines always get the full score: they are few, and on folded paper they are often the only
        // hypotheses that span the whole sheet.
        var shortlist = hypotheses
            .AsParallel().AsOrdered()
            .Select(h => (h.Corners, h.Source, Quick: QuadFinder.Score(evidence, h.Corners, withInterior: false).Total))
            .AsSequential()
            .Where(h => h.Quick > 0)
            .OrderByDescending(h => h.Quick)
            .Select((h, rank) => (h, rank))
            .Where(x => x.rank < shortlistSize || x.h.Source != "lines")
            .Select(x => x.h)
            .ToList();
        var scored = shortlist
            .Select(h => new QuadFinder.Quad(OrderCorners(h.Corners), QuadFinder.Score(evidence, h.Corners).Total, h.Source))
            .OrderByDescending(q => q.Score)
            .ToList();
        // A faint real edge (white paper on a white desk) loses to a line of print just inside it: offer the best
        // outlines again with each side moved out to the next edge beyond it.
        var extended = new List<QuadFinder.Quad>();
        foreach (var q in scored.Take(extendTop))
            for (var side = 0; side < 4; side++)
            {
                foreach (var corners in QuadFinder.ExtendSide(evidence, q.Corners, side))
                    extended.Add(new QuadFinder.Quad(OrderCorners(corners), QuadFinder.Score(evidence, corners).Total, q.Source + "+extended"));
            }
        return scored.Concat(extended).OrderByDescending(q => q.Score).ToList();
    }

    internal static int ExtendTop = 6;

    /// <summary>Quadrilateral for a region outline: rough corners from the hull, then straight-line corners.</summary>
    static Point2d[]? RegionQuad(Point[] contour)
    {
        var points = contour.Select(p => new Point2d(p.X, p.Y)).ToList();
        if (points.Count < 12) return null;
        if (Geometry.SignedArea(points) < 0) points.Reverse(); // clockwise on screen
        var hull = Cv2.ConvexHull(contour);
        var rough = QuadFit.Fit(hull);
        if (rough is null) return null;
        var centre = new Point2d(points.Average(p => p.X), points.Average(p => p.Y));
        var ordered = OrderCorners(rough.Select(p => new Point2d(p.X, p.Y)).ToArray());
        return QuadFinder.LineCorners(points, ordered) ?? ordered;
    }

    /// <summary>For diagnostics in tests: the winning hypothesis.</summary>
    internal static string Describe(Mat image)
    {
        var scale = Math.Min(1.0, WorkEdge / (double)Math.Max(image.Width, image.Height));
        using var small = new Mat();
        Cv2.Resize(image, small, new Size(), scale, scale, InterpolationFlags.Area);
        var best = FindPage(small);
        return best is null ? "no page" : $"{best.Source} score {best.Score:F2}";
    }

    // ---- candidate generation -------------------------------------------------------------------------------

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
        Cv2.GaussianBlur(cleaned, smooth, new Size(0, 0), 2.5);
        using var edges = new Mat();
        Cv2.Canny(smooth, edges, 3.5, 9);
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

    /// <summary>
    /// Orders four corners clockwise as TL, TR, BR, BL. "Top" is the edge that points most to the right when
    /// walking clockwise, i.e. the page is upright as the photo shows it.
    /// </summary>
    internal static Point2d[] OrderCorners(Point2d[] corners)
    {
        var centre = new Point2d(corners.Average(c => c.X), corners.Average(c => c.Y));
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
