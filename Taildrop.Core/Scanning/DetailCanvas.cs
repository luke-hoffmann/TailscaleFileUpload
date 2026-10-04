using System.Runtime.InteropServices;
using OpenCvSharp;
using OpenCvSharp.Features2D;

namespace Taildrop.Core.Scanning;

/// <summary>What one camera frame did for the page.</summary>
/// <param name="Accepted">The frame was placed on the page (it may still have added nothing new).</param>
/// <param name="Problem">"lost" (the page could not be found in it) or "blurry", when not accepted.</param>
/// <param name="Footprint">Where on the page the frame was (normalized page coordinates, TL TR BR BL), when known.</param>
/// <param name="Improved">Some part of the page got sharper.</param>
public sealed record DetailStep(bool Accepted, string? Problem, double[][]? Footprint, bool Improved);

/// <summary>
/// Builds a page at a higher resolution than any single camera frame has. The first frame, the whole page, is
/// flattened at the target resolution: complete, but soft wherever the camera had fewer pixels than the page needs.
/// Every later frame (typically the phone held closer over part of the page) is located on the page and, wherever
/// it saw the paper with more pixels and sharply, replaces what is there: matched to the page's brightness so no
/// patches show, and feathered in so no seams show. Frames are always placed against the first frame, so errors
/// never accumulate, then fine-aligned against the page as it is now.
///
/// A grid of cells tracks where the page already has enough detail. Blank paper needs none, so only the print
/// has to be covered. Not thread-safe: one live session feeds one canvas, a frame at a time.
/// </summary>
public sealed class DetailCanvas : IDisposable
{
    /// <summary>A4 at 300 dpi (Letter comes out at about 320 dpi).</summary>
    public const int DefaultTargetLongEdge = 3508;
    /// <summary>A page far away in the first frame would otherwise ask for more close-ups than anyone will take.</summary>
    const double MaxMagnification = 4.0;
    /// <summary>The quality map holds one value per QualityCell x QualityCell page pixels.</summary>
    const int QualityCell = 8;
    /// <summary>Camera pixels per page pixel that count as full detail.</summary>
    const double EnoughDensity = 0.9;
    const double CompleteAt = 0.97;
    const int CellsOnLongEdge = 12;
    /// <summary>Long edge of the page image the first frame's features are found on, and of frames when locating them.</summary>
    const int RegistrationEdge = 1600, FrameRegistrationEdge = 1280;
    /// <summary>Fine alignment: template size, search radius, grid of templates (work pixels).</summary>
    const int Patch = 41, Search = 12, PatchGrid = 8;
    /// <summary>A frame this much blurrier than the sharpest one so far is left out.</summary>
    const double MinRelativeSharpness = 0.6;

    readonly Mat _page;
    readonly float[] _quality;
    readonly int _qualityWidth, _qualityHeight;
    readonly bool[] _blank;
    readonly float[] _level;
    readonly KeyPoint[] _referenceKeys;
    readonly Mat _referenceDescriptors = new();
    readonly double _referenceScale;
    readonly SIFT _sift = SIFT.Create(4000);
    double _bestSharpness;

    public int Width { get; }
    public int Height { get; }
    /// <summary>Detail grid, row-major, <see cref="Columns"/> x <see cref="Rows"/> cells.</summary>
    public int Columns { get; }
    public int Rows { get; }
    /// <summary>Share of the page (0-1) that has enough detail; blank cells always do.</summary>
    public double Progress { get; private set; }
    public bool Complete => Progress >= CompleteAt;
    /// <summary>Frames placed on the page so far, not counting the first.</summary>
    public int FramesUsed { get; private set; }

    /// <summary>Starts a page from a frame showing all of it and the page's outline in that frame.</summary>
    public static DetailCanvas Start(Mat reference, ScanOutline outline, int targetLongEdge = DefaultTargetLongEdge)
    {
        var corners = outline.Corners.Select(c => new Point2d(c[0] * reference.Width, c[1] * reference.Height)).ToArray();
        var (naturalWidth, naturalHeight) = PageFlattener.OutputSize(corners, reference.Width, reference.Height);
        var natural = Math.Max(naturalWidth, naturalHeight);
        var longEdge = Math.Max(natural, Math.Min(targetLongEdge, natural * MaxMagnification));
        var scale = longEdge / natural;
        var width = Math.Max(64, (int)Math.Round(naturalWidth * scale));
        var height = Math.Max(64, (int)Math.Round(naturalHeight * scale));

        var page = PageFlattener.Flatten(reference, outline, new Size(width, height));
        var pageToPhoto = Geometry.Homography(
            new[] { new Point2d(0, 0), new Point2d(width, 0), new Point2d(width, height), new Point2d(0, height) }, corners);
        return new DetailCanvas(page, pageToPhoto, scale);
    }

    DetailCanvas(Mat page, double[] pageToPhoto, double upscale)
    {
        _page = page;
        Width = page.Width;
        Height = page.Height;

        // What the first frame already gives: its pixels per page pixel, everywhere.
        _qualityWidth = (Width + QualityCell - 1) / QualityCell;
        _qualityHeight = (Height + QualityCell - 1) / QualityCell;
        _quality = new float[_qualityWidth * _qualityHeight];
        for (var qy = 0; qy < _qualityHeight; qy++)
            for (var qx = 0; qx < _qualityWidth; qx++)
                _quality[qy * _qualityWidth + qx] = (float)Geometry.Scale(pageToPhoto, (qx + 0.5) * QualityCell, (qy + 0.5) * QualityCell);

        var cell = Math.Max(Width, Height) / (double)CellsOnLongEdge;
        Columns = Math.Max(3, (int)Math.Round(Width / cell));
        Rows = Math.Max(3, (int)Math.Round(Height / cell));
        _level = new float[Columns * Rows];
        _blank = FindBlankCells(upscale);

        // Features of the whole page, found once: every frame is located against these.
        _referenceScale = Math.Min(1.0, RegistrationEdge / (double)Math.Max(Width, Height));
        using var gray = _page.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var small = new Mat();
        Cv2.Resize(gray, small, new Size(), _referenceScale, _referenceScale, InterpolationFlags.Area);
        _sift.DetectAndCompute(small, null, out _referenceKeys, _referenceDescriptors);

        UpdateLevels();
    }

    /// <summary>Detail of each grid cell, 0-100, row-major.</summary>
    public int[] Levels() => _level.Select(l => (int)Math.Round(Math.Clamp(l, 0, 1) * 100)).ToArray();

    /// <summary>Copy of the page as it is now (flat, upright, CV_8UC3).</summary>
    public Mat Snapshot() => _page.Clone();

    /// <summary>Small JPEG of the page as it is now, for the phone's detail map.</summary>
    public byte[] Thumbnail(int longEdge = 360)
    {
        var scale = Math.Min(1.0, longEdge / (double)Math.Max(Width, Height));
        using var small = new Mat();
        Cv2.Resize(_page, small, new Size(), scale, scale, InterpolationFlags.Area);
        Cv2.ImEncode(".jpg", small, out var bytes, new ImageEncodingParam(ImwriteFlags.JpegQuality, 72));
        return bytes;
    }

    /// <summary>Locates a camera frame on the page and takes from it whatever is sharper than the page so far.</summary>
    public DetailStep Add(Mat frame)
    {
        using var gray = frame.CvtColor(ColorConversionCodes.BGR2GRAY);
        var located = Locate(gray, out var matches);
        if (located is null) return new DetailStep(false, "lost", null, false);

        // Fine alignment against the page as it is now. A close-up of almost blank paper has nothing to align on:
        // a strong first match is trusted then (there is no print to misplace), a weak one is not.
        var toPage = Refine(gray, located) ?? (matches >= 60 ? located : null);
        if (toPage is null || !Plausible(toPage, frame.Width, frame.Height))
            return new DetailStep(false, "lost", null, false);
        var footprint = Footprint(toPage, frame.Width, frame.Height);

        var sharpness = Sharpness(gray);
        var factor = 1.0;
        if (!double.IsNaN(sharpness) && _bestSharpness > 0)
        {
            factor = Math.Min(1.0, sharpness / (0.85 * _bestSharpness));
            if (factor < MinRelativeSharpness) return new DetailStep(false, "blurry", footprint, false);
        }
        if (!double.IsNaN(sharpness)) _bestSharpness = Math.Max(_bestSharpness, sharpness);

        var improved = Fuse(frame, toPage, factor);
        FramesUsed++;
        if (improved) UpdateLevels();
        return new DetailStep(true, null, footprint, improved);
    }

    public void Dispose()
    {
        _page.Dispose();
        _referenceDescriptors.Dispose();
        _sift.Dispose();
    }

    // ---- locating a frame ---------------------------------------------------------------------------------------

    /// <summary>Frame-to-page homography from features matched against the first frame, or null.</summary>
    double[]? Locate(Mat gray, out int inliers)
    {
        inliers = 0;
        if (_referenceKeys.Length < 16) return null;
        var scale = Math.Min(1.0, FrameRegistrationEdge / (double)Math.Max(gray.Width, gray.Height));
        using var small = new Mat();
        Cv2.Resize(gray, small, new Size(), scale, scale, InterpolationFlags.Area);
        using var descriptors = new Mat();
        _sift.DetectAndCompute(small, null, out var keys, descriptors);
        if (keys.Length < 16) return null;

        using var matcher = new FlannBasedMatcher();
        // One match per page feature: a big heading letter can attract dozens of frame features, and that many
        // "agreeing" matches would let a homography squash the whole frame onto it.
        var good = matcher.KnnMatch(descriptors, _referenceDescriptors, 2)
            .Where(m => m.Length == 2 && m[0].Distance < 0.75f * m[1].Distance)
            .Select(m => m[0])
            .GroupBy(m => m.TrainIdx)
            .Select(group => group.MinBy(m => m.Distance))
            .ToList();
        if (good.Count < 16) return null;

        // Pixel centres scale about the pixel's own centre: (x + 0.5) / s - 0.5.
        var source = good.Select(m => new Point2d((keys[m.QueryIdx].Pt.X + 0.5) / scale - 0.5, (keys[m.QueryIdx].Pt.Y + 0.5) / scale - 0.5));
        var target = good.Select(m => new Point2d((_referenceKeys[m.TrainIdx].Pt.X + 0.5) / _referenceScale - 0.5, (_referenceKeys[m.TrainIdx].Pt.Y + 0.5) / _referenceScale - 0.5));
        using var mask = new Mat();
        using var homography = Cv2.FindHomography(source, target, HomographyMethods.Ransac, 3.0 / _referenceScale, mask);
        if (homography.Empty()) return null;
        inliers = Cv2.CountNonZero(mask);
        // Print repeats itself (the same words, the same digits), so many plausible matches are wrong; a couple of
        // dozen that agree on one homography are not chance, and the fine alignment checks the result anyway.
        if (inliers < 20 || inliers < 0.1 * good.Count) return null;
        var located = ToArray(homography);
        return Plausible(located, gray.Width, gray.Height) ? located : null;
    }

    /// <summary>
    /// Sub-pixel alignment, coarse to fine: at half resolution the search reaches twice as far (the features may be
    /// a few pixels off), then full resolution pins it down.
    /// </summary>
    double[]? Refine(Mat gray, double[] toPage)
    {
        var coarse = Align(gray, toPage, 0.5) ?? toPage;
        return Align(gray, coarse, 1.0);
    }

    /// <summary>
    /// The frame is warped onto the page with the homography so far, then small patches of it are found again in the
    /// page around where they landed. The correction they agree on is folded in.
    /// </summary>
    double[]? Align(Mat gray, double[] toPage, double resolution)
    {
        var area = FootprintBounds(toPage, gray.Width, gray.Height, 0);
        if (area.Width < 96 || area.Height < 96) return null;

        var toFrame = Geometry.Invert(toPage);
        var density = Geometry.Scale(toFrame, area.X + area.Width / 2.0, area.Y + area.Height / 2.0);
        var workScale = resolution * Math.Min(1.0, Math.Min(density, 1400.0 / Math.Max(area.Width, area.Height)));
        var work = new Size(Math.Max(1, (int)Math.Ceiling(area.Width * workScale)), Math.Max(1, (int)Math.Ceiling(area.Height * workScale)));
        if (work.Width < 3 * Patch || work.Height < 3 * Patch) return null;
        var pageToWork = new[] { workScale, 0, -area.X * workScale, 0, workScale, -area.Y * workScale, 0, 0, 1 };
        var frameToWork = Geometry.Multiply(pageToWork, toPage);

        using var warped = new Mat();
        using (var transform = ToMat(frameToWork))
            Cv2.WarpPerspective(gray, warped, transform, work, InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
        using var valid = new Mat();
        using (var ones = new Mat(gray.Size(), MatType.CV_8UC1, Scalar.All(255)))
        using (var transform = ToMat(frameToWork))
            Cv2.WarpPerspective(ones, valid, transform, work, InterpolationFlags.Nearest, BorderTypes.Constant, Scalar.All(0));

        using var pageGray = new Mat();
        using (var region = new Mat(_page, area))
        using (var regionGray = region.CvtColor(ColorConversionCodes.BGR2GRAY))
            Cv2.Resize(regionGray, pageGray, work, 0, 0, InterpolationFlags.Area);

        // Same mild blur on both: the page may still be the soft first frame here while the frame is sharp.
        Cv2.GaussianBlur(warped, warped, new Size(0, 0), 1.0);
        Cv2.GaussianBlur(pageGray, pageGray, new Size(0, 0), 1.0);

        var from = new List<Point2d>();
        var to = new List<Point2d>();
        using var result = new Mat();
        for (var gy = 0; gy < PatchGrid; gy++)
            for (var gx = 0; gx < PatchGrid; gx++)
            {
                var cx = (int)Math.Round((gx + 0.5) / PatchGrid * work.Width);
                var cy = (int)Math.Round((gy + 0.5) / PatchGrid * work.Height);
                var template = new Rect(cx - Patch / 2, cy - Patch / 2, Patch, Patch);
                var window = new Rect(template.X - Search, template.Y - Search, Patch + 2 * Search, Patch + 2 * Search);
                if (window.X < 0 || window.Y < 0 || window.Right > work.Width || window.Bottom > work.Height) continue;
                using (var mask = new Mat(valid, window))
                    if (Cv2.CountNonZero(mask) < window.Width * window.Height) continue;
                using var piece = new Mat(warped, template);
                Cv2.MeanStdDev(piece, out _, out var spread);
                if (spread.Val0 < 7) continue;   // blank paper: nothing to align on

                using var searchArea = new Mat(pageGray, window);
                Cv2.MatchTemplate(searchArea, piece, result, TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(result, out _, out var best, out _, out var at);
                if (best < 0.6) continue;
                var (dx, dy) = SubPixel(result, at);
                from.Add(new Point2d(cx, cy));
                to.Add(new Point2d(window.X + at.X + dx + Patch / 2, window.Y + at.Y + dy + Patch / 2));
            }
        if (from.Count < 8) return null;

        using var inlierMask = new Mat();
        using var correction = Cv2.FindHomography(from, to, HomographyMethods.Ransac, 1.5, inlierMask);
        if (correction.Empty() || Cv2.CountNonZero(inlierMask) < Math.Max(8, 0.6 * from.Count)) return null;
        var delta = ToArray(correction);
        // A correction is small by construction; anything bigger means the patches agreed on something wrong.
        foreach (var (x, y) in new[] { (0.0, 0.0), (work.Width, 0.0), (work.Width, work.Height), (0.0, work.Height) })
        {
            var moved = Geometry.Apply(delta, x, y);
            if (Math.Abs(moved.X - x) > Search + 2 || Math.Abs(moved.Y - y) > Search + 2) return null;
        }
        return Geometry.Multiply(Geometry.Invert(pageToWork), Geometry.Multiply(delta, frameToWork));
    }

    /// <summary>Peak offset (-0.5..0.5) from a parabola through the best match and its neighbours.</summary>
    static (double Dx, double Dy) SubPixel(Mat scores, Point at)
    {
        static double Vertex(float left, float centre, float right)
        {
            var denominator = left - 2 * centre + right;
            return Math.Abs(denominator) < 1e-6 ? 0 : Math.Clamp(0.5 * (left - right) / denominator, -0.5, 0.5);
        }
        double dx = 0, dy = 0;
        if (at.X > 0 && at.X < scores.Width - 1)
            dx = Vertex(scores.At<float>(at.Y, at.X - 1), scores.At<float>(at.Y, at.X), scores.At<float>(at.Y, at.X + 1));
        if (at.Y > 0 && at.Y < scores.Height - 1)
            dy = Vertex(scores.At<float>(at.Y - 1, at.X), scores.At<float>(at.Y, at.X), scores.At<float>(at.Y + 1, at.X));
        return (dx, dy);
    }

    /// <summary>The frame lands on the page right side up, at a believable size and distance.</summary>
    bool Plausible(double[] toPage, int frameWidth, int frameHeight)
    {
        var corners = FrameCorners(frameWidth, frameHeight).Select(c => Geometry.Apply(toPage, c.X, c.Y)).ToArray();
        if (corners.Any(c => !double.IsFinite(c.X) || !double.IsFinite(c.Y)) || !QuadFinder.IsConvex(corners)) return false;
        if (Geometry.SignedArea(corners) <= 0) return false;   // mirrored
        var area = Geometry.SignedArea(corners) / (Width * (double)Height);
        if (area is < 0.003 or > 25) return false;
        var centre = Geometry.Apply(toPage, frameWidth / 2.0, frameHeight / 2.0);
        if (centre.X < 0 || centre.Y < 0 || centre.X > Width || centre.Y > Height) return false;
        var density = Geometry.Scale(Geometry.Invert(toPage), centre.X, centre.Y);
        return density is > 0.1 and < 10;
    }

    // ---- taking detail from a frame -----------------------------------------------------------------------------

    /// <summary>Blends the frame into the page wherever it is clearly sharper. Returns whether anything changed.</summary>
    bool Fuse(Mat frame, double[] toPage, double sharpness)
    {
        var area = FootprintBounds(toPage, frame.Width, frame.Height, QualityCell);
        // Snap to the quality grid so every quality value covers whole page pixels of the area.
        var qx0 = area.X / QualityCell;
        var qy0 = area.Y / QualityCell;
        var qx1 = Math.Min(_qualityWidth, (area.Right + QualityCell - 1) / QualityCell);
        var qy1 = Math.Min(_qualityHeight, (area.Bottom + QualityCell - 1) / QualityCell);
        var qw = qx1 - qx0;
        var qh = qy1 - qy0;
        if (qw < 2 || qh < 2) return false;
        area = new Rect(qx0 * QualityCell, qy0 * QualityCell, Math.Min(qw * QualityCell, Width - qx0 * QualityCell), Math.Min(qh * QualityCell, Height - qy0 * QualityCell));

        // Quality the frame offers at each quality cell: its pixels per page pixel, discounted for blur and, toward
        // its own borders, faded to nothing (lens corners are softest, and the fade becomes the feathering).
        var toFrame = Geometry.Invert(toPage);
        var shortSide = Math.Min(frame.Width, frame.Height);
        var alpha = new float[qw * qh];
        var inside = new float[qw * qh];
        var updated = new float[qw * qh];
        var any = false;
        for (var y = 0; y < qh; y++)
            for (var x = 0; x < qw; x++)
            {
                var px = (qx0 + x + 0.5) * QualityCell;
                var py = (qy0 + y + 0.5) * QualityCell;
                var f = Geometry.Apply(toFrame, px, py);
                var border = Math.Min(Math.Min(f.X, f.Y), Math.Min(frame.Width - 1 - f.X, frame.Height - 1 - f.Y)) / shortSide;
                if (!(border > 0)) continue;
                inside[y * qw + x] = 1;
                var offered = Geometry.Scale(toFrame, px, py) * sharpness * SmoothStep(0.02, 0.15, border);
                var index = (qy0 + y) * _qualityWidth + qx0 + x;
                var current = _quality[index];
                var weight = current <= 0 ? 1 : SmoothStep(1.05, 1.6, offered / current);
                if (weight <= 0) continue;
                alpha[y * qw + x] = (float)weight;
                updated[y * qw + x] = (float)(current + weight * (offered - current));
                any = true;
            }
        if (!any) return false;

        using var patch = new Mat();
        var toArea = Geometry.Multiply(new double[] { 1, 0, -area.X, 0, 1, -area.Y, 0, 0, 1 }, toPage);
        using (var transform = ToMat(toArea))
            Cv2.WarpPerspective(frame, patch, transform, area.Size, InterpolationFlags.Cubic, BorderTypes.Replicate);

        using var region = new Mat(_page, area);
        using var gain = MatchBrightness(region, patch, inside, qw, qh);
        using var alphaSmall = FromArray(alpha, qw, qh);
        using var alphaFull = new Mat();
        Cv2.Resize(alphaSmall, alphaFull, area.Size, 0, 0, InterpolationFlags.Linear);

        using var current32 = new Mat();
        using var patch32 = new Mat();
        region.ConvertTo(current32, MatType.CV_32FC3);
        patch.ConvertTo(patch32, MatType.CV_32FC3);
        Cv2.Multiply(patch32, gain, patch32);
        using var alpha3 = new Mat();
        Cv2.Merge(new[] { alphaFull, alphaFull, alphaFull }, alpha3);
        using var difference = new Mat();
        Cv2.Subtract(patch32, current32, difference);
        Cv2.Multiply(difference, alpha3, difference);
        Cv2.Add(current32, difference, current32);
        current32.ConvertTo(region, MatType.CV_8UC3);

        for (var y = 0; y < qh; y++)
            for (var x = 0; x < qw; x++)
                if (alpha[y * qw + x] > 0) _quality[(qy0 + y) * _qualityWidth + qx0 + x] = updated[y * qw + x];
        return true;
    }

    /// <summary>
    /// Per-pixel, per-channel gain that gives the frame the page's large-scale brightness and color: the camera
    /// re-exposes as it moves closer, and the phone's own shadow falls on the paper. Only fine detail is taken.
    /// </summary>
    static Mat MatchBrightness(Mat region, Mat patch, float[] inside, int qw, int qh)
    {
        var size = new Size(qw, qh);
        using var regionSmall = new Mat();
        using var patchSmall = new Mat();
        Cv2.Resize(region, regionSmall, size, 0, 0, InterpolationFlags.Area);
        Cv2.Resize(patch, patchSmall, size, 0, 0, InterpolationFlags.Area);
        regionSmall.ConvertTo(regionSmall, MatType.CV_32FC3);
        patchSmall.ConvertTo(patchSmall, MatType.CV_32FC3);

        using var mask = FromArray(inside, qw, qh);
        using var mask3 = new Mat();
        Cv2.Merge(new[] { mask, mask, mask }, mask3);
        var sigma = 5.0;   // quality cells: about 40 page pixels
        using var maskBlur = new Mat();
        Cv2.GaussianBlur(mask3, maskBlur, new Size(0, 0), sigma);
        using var patchMasked = new Mat();
        Cv2.Multiply(patchSmall, mask3, patchMasked);
        using var patchBlur = new Mat();
        Cv2.GaussianBlur(patchMasked, patchBlur, new Size(0, 0), sigma);
        using var regionMasked = new Mat();
        Cv2.Multiply(regionSmall, mask3, regionMasked);
        using var regionBlur = new Mat();
        Cv2.GaussianBlur(regionMasked, regionBlur, new Size(0, 0), sigma);

        // Normalized convolution on both sides, over the same pixels, so the frame's edge doesn't bias either.
        using var floor = new Mat(size, MatType.CV_32FC3, Scalar.All(1e-3));
        Cv2.Max(maskBlur, floor, maskBlur);
        Cv2.Divide(patchBlur, maskBlur, patchBlur);
        Cv2.Divide(regionBlur, maskBlur, regionBlur);
        Cv2.Add(patchBlur, Scalar.All(8), patchBlur);
        Cv2.Add(regionBlur, Scalar.All(8), regionBlur);
        var gainSmall = new Mat();
        Cv2.Divide(regionBlur, patchBlur, gainSmall);
        Cv2.Max(gainSmall, Scalar.All(0.6), gainSmall);
        Cv2.Min(gainSmall, Scalar.All(1.7), gainSmall);

        var gain = new Mat();
        Cv2.Resize(gainSmall, gain, region.Size(), 0, 0, InterpolationFlags.Linear);
        gainSmall.Dispose();
        return gain;
    }

    // ---- detail grid --------------------------------------------------------------------------------------------

    /// <summary>
    /// Cells without print (margins, gaps between paragraphs) never need more detail. Judged on the first frame at
    /// its own resolution: print shows as strong local contrast, bare paper only as noise and gentle shading.
    /// </summary>
    bool[] FindBlankCells(double upscale)
    {
        using var gray = _page.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var natural = new Mat();
        Cv2.Resize(gray, natural, new Size(), 1 / upscale, 1 / upscale, InterpolationFlags.Area);
        using var smooth = new Mat();
        Cv2.GaussianBlur(natural, smooth, new Size(0, 0), 3);
        using var detail = new Mat();
        Cv2.Absdiff(natural, smooth, detail);

        var blank = new bool[Columns * Rows];
        for (var row = 0; row < Rows; row++)
            for (var col = 0; col < Columns; col++)
            {
                var x0 = col * natural.Width / Columns;
                var y0 = row * natural.Height / Rows;
                var x1 = (col + 1) * natural.Width / Columns;
                var y1 = (row + 1) * natural.Height / Rows;
                if (x1 - x0 < 2 || y1 - y0 < 2) { blank[row * Columns + col] = true; continue; }
                using var cell = new Mat(detail, new Rect(x0, y0, x1 - x0, y1 - y0));
                blank[row * Columns + col] = Percentile(cell, 0.99) < BlankContrast;
            }
        return blank;
    }

    /// <summary>Gray levels of local contrast (99th percentile in a cell) below which the cell is bare paper.</summary>
    internal static double BlankContrast = 14;

    static double Percentile(Mat cell8u, double fraction)
    {
        using var histogram = new Mat();
        Cv2.CalcHist(new[] { cell8u }, new[] { 0 }, null, histogram, 1, new[] { 256 }, new[] { new Rangef(0, 256) });
        var total = cell8u.Width * (double)cell8u.Height;
        double seen = 0;
        for (var v = 0; v < 256; v++)
        {
            seen += histogram.At<float>(v);
            if (seen >= fraction * total) return v;
        }
        return 255;
    }

    void UpdateLevels()
    {
        double sum = 0;
        for (var row = 0; row < Rows; row++)
            for (var col = 0; col < Columns; col++)
            {
                var index = row * Columns + col;
                if (_blank[index]) { _level[index] = 1; sum += 1; continue; }
                var qx0 = col * _qualityWidth / Columns;
                var qy0 = row * _qualityHeight / Rows;
                var qx1 = Math.Max(qx0 + 1, (col + 1) * _qualityWidth / Columns);
                var qy1 = Math.Max(qy0 + 1, (row + 1) * _qualityHeight / Rows);
                double cell = 0;
                for (var qy = qy0; qy < qy1; qy++)
                    for (var qx = qx0; qx < qx1; qx++)
                        cell += Math.Min(1.0, _quality[qy * _qualityWidth + qx] / EnoughDensity);
                var level = cell / ((qx1 - qx0) * (qy1 - qy0));
                _level[index] = (float)level;
                sum += level;
            }
        Progress = sum / _level.Length;
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    /// <summary>
    /// How crisp the frame's edges are: the steepest slope across an edge divided by the edge's contrast, so
    /// proportional to one over the edge's width in pixels whatever the print. Edges are found at quarter resolution, where
    /// even a badly shaken edge is still a clear step, and judged in four directions: shake blurs one direction
    /// only, so the worst direction is the answer. NaN when there is too little print to judge by.
    /// </summary>
    internal static double Sharpness(Mat gray)
    {
        // The middle of the frame (lens corners are always softer), at most 1200 px square: plenty of print to judge.
        var width = Math.Min(gray.Width * 8 / 10, 1200);
        var height = Math.Min(gray.Height * 8 / 10, 1200);
        using var centre = new Mat(gray, new Rect((gray.Width - width) / 2, (gray.Height - height) / 2, width, height));
        using var fine = new Mat();
        centre.ConvertTo(fine, MatType.CV_32F);
        using var half = new Mat();
        using var quarter = new Mat();
        Cv2.PyrDown(fine, half);
        Cv2.PyrDown(half, quarter);
        using var neighbourhood = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(7, 7));

        var worst = double.PositiveInfinity;
        var measured = 0;
        var counts = new List<int>();
        foreach (var (first, across) in DirectionKernels.Value)
        {
            // Edges this direction can judge: a strong step along it, stronger than across it.
            using var step = new Mat();
            using var stepAcross = new Mat();
            Cv2.Filter2D(quarter, step, MatType.CV_32F, first);
            Cv2.Filter2D(quarter, stepAcross, MatType.CV_32F, across);
            using var contrast = Cv2.Abs(step).ToMat();
            using var acrossAbs = Cv2.Abs(stepAcross).ToMat();
            using var strong = new Mat();
            Cv2.Compare(contrast, new Scalar(60), strong, CmpTypes.GT);
            using var dominant = new Mat();
            Cv2.Compare(contrast, acrossAbs, dominant, CmpTypes.GE);
            using var edges = new Mat();
            Cv2.BitwiseAnd(strong, dominant, edges);
            counts.Add(Cv2.CountNonZero(edges));
            if (counts[^1] < 0.002 * quarter.Width * quarter.Height) continue;

            // Steepest full-resolution slope near each edge, brought down to the edge grid.
            using var slope = new Mat();
            Cv2.Filter2D(fine, slope, MatType.CV_32F, first);
            using var slopeAbs = Cv2.Abs(slope).ToMat();
            using var peak = new Mat();
            Cv2.Dilate(slopeAbs, peak, neighbourhood);
            using var peakHalf = new Mat();
            using var peakQuarter = new Mat();
            Cv2.Resize(peak, peakHalf, half.Size(), 0, 0, InterpolationFlags.Nearest);
            Cv2.Resize(peakHalf, peakQuarter, quarter.Size(), 0, 0, InterpolationFlags.Nearest);

            var meanContrast = Cv2.Mean(contrast, edges).Val0;
            if (meanContrast <= 0) continue;
            worst = Math.Min(worst, Cv2.Mean(peakQuarter, edges).Val0 / meanContrast);
            measured++;
        }
        if (measured < 2) return double.NaN;
        // Shaken hard enough, one direction has no edges left at all; print always has edges every way.
        var balance = counts.Min() / (double)counts.Max();
        return balance < 0.15 ? worst * balance / 0.15 : worst;
    }

    /// <summary>Per direction (horizontal, vertical, both diagonals): the step along it and the step across it.</summary>
    static readonly Lazy<(Mat Along, Mat Across)[]> DirectionKernels = new(() =>
    {
        static Mat Kernel(params float[] values)
        {
            var mat = new Mat(3, 3, MatType.CV_32FC1);
            for (var i = 0; i < 9; i++) mat.Set(i / 3, i % 3, values[i]);
            return mat;
        }
        Mat dx = Kernel(0, 0, 0, -1, 0, 1, 0, 0, 0), dy = Kernel(0, -1, 0, 0, 0, 0, 0, 1, 0);
        Mat dd = Kernel(-1, 0, 0, 0, 0, 0, 0, 0, 1), da = Kernel(0, 0, -1, 0, 0, 0, 1, 0, 0);
        return new[] { (dx, dy), (dy, dx), (dd, da), (da, dd) };
    });

    static double SmoothStep(double edge0, double edge1, double x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    static Point2d[] FrameCorners(int width, int height) =>
        new[] { new Point2d(0, 0), new Point2d(width - 1, 0), new Point2d(width - 1, height - 1), new Point2d(0, height - 1) };

    double[][] Footprint(double[] toPage, int frameWidth, int frameHeight) =>
        FrameCorners(frameWidth, frameHeight)
            .Select(c => Geometry.Apply(toPage, c.X, c.Y))
            .Select(p => new[] { Math.Round(p.X / Width, 4), Math.Round(p.Y / Height, 4) })
            .ToArray();

    /// <summary>The page area the frame covers (plus a margin), clipped to the page.</summary>
    Rect FootprintBounds(double[] toPage, int frameWidth, int frameHeight, int margin)
    {
        var corners = FrameCorners(frameWidth, frameHeight).Select(c => Geometry.Apply(toPage, c.X, c.Y)).ToArray();
        var x0 = Math.Clamp((int)Math.Floor(corners.Min(c => c.X)) - margin, 0, Width);
        var y0 = Math.Clamp((int)Math.Floor(corners.Min(c => c.Y)) - margin, 0, Height);
        var x1 = Math.Clamp((int)Math.Ceiling(corners.Max(c => c.X)) + margin, 0, Width);
        var y1 = Math.Clamp((int)Math.Ceiling(corners.Max(c => c.Y)) + margin, 0, Height);
        return new Rect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    static double[] ToArray(Mat homography)
    {
        var result = new double[9];
        for (var r = 0; r < 3; r++)
            for (var c = 0; c < 3; c++)
                result[r * 3 + c] = homography.At<double>(r, c);
        return result;
    }

    static Mat ToMat(double[] m)
    {
        var mat = new Mat(3, 3, MatType.CV_64FC1);
        for (var r = 0; r < 3; r++)
            for (var c = 0; c < 3; c++)
                mat.Set(r, c, m[r * 3 + c]);
        return mat;
    }

    static Mat FromArray(float[] values, int width, int height)
    {
        var mat = new Mat(height, width, MatType.CV_32FC1);
        for (var y = 0; y < height; y++) Marshal.Copy(values, y * width, mat.Ptr(y), width);
        return mat;
    }
}
