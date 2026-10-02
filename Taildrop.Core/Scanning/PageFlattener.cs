using System.Runtime.InteropServices;
using OpenCvSharp;

namespace Taildrop.Core.Scanning;

/// <summary>
/// Turns a photo plus a page outline into a flat, upright, full-resolution page.
/// Perspective is removed with a homography through the four corners; whatever bend remains in the
/// four edge curves is then removed with a Coons patch (a smooth surface spanning the four boundary
/// curves), so a curled receipt or a folded sheet comes out rectangular with nothing cut off.
/// </summary>
public static class PageFlattener
{
    const int MaxLongEdge = 6000;
    const long MaxPixels = 24_000_000;
    const int CellSize = 24;
    const int StripRows = 256;

    static readonly double[] StandardRatios = { 297.0 / 210.0, 11.0 / 8.5 };

    public static Mat Flatten(Mat image, ScanOutline outline)
    {
        var imageWidth = image.Width;
        var imageHeight = image.Height;

        var corners = outline.Corners.Select(c => new Point2d(c[0] * imageWidth, c[1] * imageHeight)).ToArray();
        var (width, height) = OutputSize(corners, imageWidth, imageHeight);

        // Homography from the photo to a flat width x height page.
        var toPage = Geometry.Homography(corners, new[]
        {
            new Point2d(0, 0), new Point2d(width, 0), new Point2d(width, height), new Point2d(0, height)
        });
        var toPhoto = Geometry.Invert(toPage);

        // The four edge curves expressed in the flat page's coordinates (they are nearly straight lines
        // there, except where the paper really is bent).
        var top = ToPage(outline.Top, toPage, imageWidth, imageHeight);
        var right = ToPage(outline.Right, toPage, imageWidth, imageHeight);
        var bottom = ToPage(outline.Bottom, toPage, imageWidth, imageHeight);
        var left = ToPage(outline.Left, toPage, imageWidth, imageHeight);

        // Coarse grid of "where in the photo does this page point come from", via the Coons patch.
        var nx = Math.Clamp(width / CellSize + 1, 9, 301);
        var ny = Math.Clamp(height / CellSize + 1, 9, 301);
        var gridX = new double[ny, nx];
        var gridY = new double[ny, nx];
        for (var j = 0; j < ny; j++)
        {
            var t = j / (double)(ny - 1);
            for (var i = 0; i < nx; i++)
            {
                var s = i / (double)(nx - 1);
                var onPage = Coons(top, right, bottom, left, s, t);
                var source = Geometry.Apply(toPhoto, onPage.X, onPage.Y);
                gridX[j, i] = source.X - 0.5; // continuous coordinates -> pixel-index coordinates
                gridY[j, i] = source.Y - 0.5;
            }
        }

        // Per-column interpolation weights, shared by every row.
        var columnIndex = new int[width];
        var columnFraction = new float[width];
        for (var u = 0; u < width; u++)
        {
            var position = (u + 0.5) / width * (nx - 1);
            var index = Math.Min((int)position, nx - 2);
            columnIndex[u] = index;
            columnFraction[u] = (float)(position - index);
        }

        var result = new Mat(height, width, MatType.CV_8UC3);
        using var emptyMap = new Mat();
        for (var start = 0; start < height; start += StripRows)
        {
            var rows = Math.Min(StripRows, height - start);
            using var map = new Mat(rows, width, MatType.CV_32FC2);
            var stripStart = start;
            Parallel.For(0, rows, r =>
            {
                var position = (stripStart + r + 0.5) / height * (ny - 1);
                var j0 = Math.Min((int)position, ny - 2);
                var fj = (float)(position - j0);
                var line = new float[width * 2];
                for (var u = 0; u < width; u++)
                {
                    var i0 = columnIndex[u];
                    var fi = columnFraction[u];
                    var x00 = gridX[j0, i0]; var x10 = gridX[j0, i0 + 1];
                    var x01 = gridX[j0 + 1, i0]; var x11 = gridX[j0 + 1, i0 + 1];
                    var y00 = gridY[j0, i0]; var y10 = gridY[j0, i0 + 1];
                    var y01 = gridY[j0 + 1, i0]; var y11 = gridY[j0 + 1, i0 + 1];
                    line[2 * u] = (float)((x00 * (1 - fi) + x10 * fi) * (1 - fj) + (x01 * (1 - fi) + x11 * fi) * fj);
                    line[2 * u + 1] = (float)((y00 * (1 - fi) + y10 * fi) * (1 - fj) + (y01 * (1 - fi) + y11 * fi) * fj);
                }
                Marshal.Copy(line, 0, map.Ptr(r), width * 2);
            });

            using var strip = result.RowRange(start, start + rows);
            Cv2.Remap(image, strip, map, emptyMap, InterpolationFlags.Cubic, BorderTypes.Replicate);
        }
        return result;
    }

    /// <summary>
    /// An edge curve in flat-page coordinates, re-spaced evenly by arc length. The outline's samples are spaced
    /// evenly in the photo, which under perspective is not even along the paper; on the flat page, distance is
    /// physical, so even spacing there means "the same fraction of the way along the real edge".
    /// </summary>
    static Point2d[] ToPage(double[][] edge, double[] toPage, int imageWidth, int imageHeight) =>
        Geometry.ResampleByArcLength(
            edge.Select(p => Geometry.Apply(toPage, p[0] * imageWidth, p[1] * imageHeight)).ToArray(), edge.Length);

    static Point2d Sample(Point2d[] curve, double t)
    {
        var position = Math.Clamp(t, 0, 1) * (curve.Length - 1);
        var lo = Math.Min((int)position, curve.Length - 2);
        var f = position - lo;
        return new Point2d(curve[lo].X + (curve[lo + 1].X - curve[lo].X) * f, curve[lo].Y + (curve[lo + 1].Y - curve[lo].Y) * f);
    }

    /// <summary>Bilinearly blended Coons patch through four boundary curves (top/bottom run in s, left/right in t).</summary>
    static Point2d Coons(Point2d[] top, Point2d[] right, Point2d[] bottom, Point2d[] left, double s, double t)
    {
        var a = Sample(top, s);
        var b = Sample(bottom, s);
        var c = Sample(left, t);
        var d = Sample(right, t);
        Point2d tl = top[0], tr = top[^1], bl = bottom[0], br = bottom[^1];
        var x = (1 - t) * a.X + t * b.X + (1 - s) * c.X + s * d.X
                - ((1 - s) * (1 - t) * tl.X + s * (1 - t) * tr.X + (1 - s) * t * bl.X + s * t * br.X);
        var y = (1 - t) * a.Y + t * b.Y + (1 - s) * c.Y + s * d.Y
                - ((1 - s) * (1 - t) * tl.Y + s * (1 - t) * tr.Y + (1 - s) * t * bl.Y + s * t * br.Y);
        return new Point2d(x, y);
    }

    /// <summary>Output size in pixels: true page proportions, at the photo's own (near-side) resolution, never upscaled.</summary>
    internal static (int Width, int Height) OutputSize(Point2d[] corners, int imageWidth, int imageHeight)
    {
        double topLength = Geometry.Distance(corners[0], corners[1]);
        double rightLength = Geometry.Distance(corners[1], corners[2]);
        double bottomLength = Geometry.Distance(corners[3], corners[2]);
        double leftLength = Geometry.Distance(corners[0], corners[3]);

        var chordRatio = (topLength + bottomLength) / Math.Max(leftLength + rightLength, 1e-6);
        var ratio = EstimateAspectRatio(corners, imageWidth, imageHeight, chordRatio);
        ratio = SnapToPaperSize(ratio);

        var scale = Math.Max(Math.Max(topLength, bottomLength) / ratio, Math.Max(leftLength, rightLength));
        var width = scale * ratio;
        var height = scale;

        var longEdge = Math.Max(width, height);
        var shrink = Math.Min(1.0, MaxLongEdge / longEdge);
        shrink = Math.Min(shrink, Math.Sqrt(MaxPixels / (width * height)));
        width *= shrink;
        height *= shrink;
        return (Math.Max(64, (int)Math.Round(width)), Math.Max(64, (int)Math.Round(height)));
    }

    /// <summary>
    /// True width/height of a rectangle photographed at an angle (Zhang &amp; He, "Whiteboard scanning and image
    /// enhancement"), assuming the principal point is the image centre. Falls back to the plain edge-length
    /// ratio when the page is nearly facing the camera (the estimate is unstable there) or the maths degenerates.
    /// </summary>
    internal static double EstimateAspectRatio(Point2d[] corners, int imageWidth, int imageHeight, double chordRatio)
    {
        double cx = imageWidth / 2.0, cy = imageHeight / 2.0;
        // m1 = TL, m2 = TR, m3 = BL, m4 = BR, relative to the principal point.
        double[] m1 = { corners[0].X - cx, corners[0].Y - cy, 1 };
        double[] m2 = { corners[1].X - cx, corners[1].Y - cy, 1 };
        double[] m3 = { corners[3].X - cx, corners[3].Y - cy, 1 };
        double[] m4 = { corners[2].X - cx, corners[2].Y - cy, 1 };

        var k2Denominator = Dot(Cross(m2, m4), m3);
        var k3Denominator = Dot(Cross(m3, m4), m2);
        if (Math.Abs(k2Denominator) < 1e-9 || Math.Abs(k3Denominator) < 1e-9) return chordRatio;

        var k2 = Dot(Cross(m1, m4), m3) / k2Denominator;
        var k3 = Dot(Cross(m1, m4), m2) / k3Denominator;
        double[] n2 = { k2 * m2[0] - m1[0], k2 * m2[1] - m1[1], k2 * m2[2] - m1[2] };
        double[] n3 = { k3 * m3[0] - m1[0], k3 * m3[1] - m1[1], k3 * m3[2] - m1[2] };

        // Both vanishing points finite -> the focal length can be estimated. If one pair of edges stays parallel in
        // the photo (page tilted about a single axis, the usual phone pose) it cannot, so assume a typical phone
        // main camera (~0.75 x the long edge); the ratio depends only weakly on it. Nearly facing the camera,
        // the estimate is noise, so trust the edge lengths.
        var n2Usable = Math.Abs(n2[2]) >= 0.04;
        var n3Usable = Math.Abs(n3[2]) >= 0.04;
        if (!n2Usable && !n3Usable) return chordRatio;

        var longEdge = Math.Max(imageWidth, imageHeight);
        var focalLength = 0.75 * longEdge;
        if (n2Usable && n3Usable)
        {
            var focalSquared = -(n2[0] * n3[0] + n2[1] * n3[1]) / (n2[2] * n3[2]);
            if (focalSquared > 0.35 * 0.35 * longEdge * longEdge && focalSquared < 2.0 * 2.0 * longEdge * longEdge)
                focalLength = Math.Sqrt(focalSquared);
        }
        var f2 = focalLength * focalLength;

        var horizontal = (n2[0] * n2[0] + n2[1] * n2[1]) / f2 + n2[2] * n2[2];
        var vertical = (n3[0] * n3[0] + n3[1] * n3[1]) / f2 + n3[2] * n3[2];
        if (!(horizontal > 0) || !(vertical > 0)) return chordRatio;

        var ratio = Math.Sqrt(horizontal / vertical);
        // A wildly different answer means the corners were not a clean rectangle; trust the photo's edges.
        return double.IsFinite(ratio) && ratio / chordRatio is > 0.5 and < 2.0 ? ratio : chordRatio;
    }

    static double SnapToPaperSize(double ratio)
    {
        foreach (var standard in StandardRatios)
        {
            if (Math.Abs(ratio / standard - 1) < 0.03) return standard;
            if (Math.Abs(ratio * standard - 1) < 0.03) return 1 / standard;
        }
        return ratio;
    }

    static double[] Cross(double[] a, double[] b) => new[]
    {
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0]
    };

    static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
}
