using OpenCvSharp;

namespace Taildrop.Core.Scanning;

static class Geometry
{
    /// <summary>Shoelace sum / 2. Positive means clockwise on screen (y grows downward).</summary>
    public static double SignedArea(IReadOnlyList<Point2d> p)
    {
        double sum = 0;
        for (var i = 0; i < p.Count; i++)
        {
            var a = p[i];
            var b = p[(i + 1) % p.Count];
            sum += a.X * b.Y - b.X * a.Y;
        }
        return sum / 2;
    }

    public static double Distance(Point2d a, Point2d b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Resamples a polyline to <paramref name="count"/> points evenly spaced by arc length.</summary>
    public static Point2d[] ResampleByArcLength(IReadOnlyList<Point2d> run, int count)
    {
        var result = new Point2d[count];
        if (run.Count == 0) return result;
        if (run.Count == 1)
        {
            Array.Fill(result, run[0]);
            return result;
        }

        var cumulative = new double[run.Count];
        for (var i = 1; i < run.Count; i++) cumulative[i] = cumulative[i - 1] + Distance(run[i - 1], run[i]);
        var total = cumulative[^1];
        if (total < 1e-9)
        {
            Array.Fill(result, run[0]);
            return result;
        }

        var segment = 0;
        for (var i = 0; i < count; i++)
        {
            var target = total * i / (count - 1);
            while (segment < run.Count - 2 && cumulative[segment + 1] < target) segment++;
            var length = cumulative[segment + 1] - cumulative[segment];
            var f = length < 1e-12 ? 0 : (target - cumulative[segment]) / length;
            result[i] = new Point2d(
                run[segment].X + (run[segment + 1].X - run[segment].X) * f,
                run[segment].Y + (run[segment + 1].Y - run[segment].Y) * f);
        }
        return result;
    }

    /// <summary>Light smoothing that keeps both end points fixed.</summary>
    public static void SmoothInPlace(Point2d[] points, int passes)
    {
        for (var pass = 0; pass < passes; pass++)
        {
            var copy = (Point2d[])points.Clone();
            for (var i = 1; i < points.Length - 1; i++)
                points[i] = new Point2d(
                    0.25 * copy[i - 1].X + 0.5 * copy[i].X + 0.25 * copy[i + 1].X,
                    0.25 * copy[i - 1].Y + 0.5 * copy[i].Y + 0.25 * copy[i + 1].Y);
        }
    }

    /// <summary>Largest distance from any point to the straight line through the first and last point.</summary>
    public static double MaxDeviationFromChord(IReadOnlyList<Point2d> run)
    {
        var a = run[0];
        var b = run[^1];
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1e-9) return 0;
        double max = 0;
        foreach (var p in run) max = Math.Max(max, Math.Abs((p.X - a.X) * dy - (p.Y - a.Y) * dx) / length);
        return max;
    }

    /// <summary>3x3 homography (row-major) mapping the four source points onto the four destination points.</summary>
    public static double[] Homography(Point2d[] source, Point2d[] destination)
    {
        using var h = Cv2.FindHomography(source, destination);
        var result = new double[9];
        for (var r = 0; r < 3; r++)
            for (var c = 0; c < 3; c++)
                result[r * 3 + c] = h.At<double>(r, c);
        return result;
    }

    public static double[] Invert(double[] m)
    {
        double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
        var det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        var inv = 1.0 / det;
        return new[]
        {
            (e * i - f * h) * inv, (c * h - b * i) * inv, (b * f - c * e) * inv,
            (f * g - d * i) * inv, (a * i - c * g) * inv, (c * d - a * f) * inv,
            (d * h - e * g) * inv, (b * g - a * h) * inv, (a * e - b * d) * inv
        };
    }

    /// <summary>Product of two row-major 3x3 matrices: applying the result is applying <paramref name="b"/>, then <paramref name="a"/>.</summary>
    public static double[] Multiply(double[] a, double[] b)
    {
        var result = new double[9];
        for (var r = 0; r < 3; r++)
            for (var c = 0; c < 3; c++)
                result[r * 3 + c] = a[r * 3] * b[c] + a[r * 3 + 1] * b[3 + c] + a[r * 3 + 2] * b[6 + c];
        return result;
    }

    /// <summary>
    /// How many destination pixels one source pixel covers at (x, y) under a homography (the square root of the
    /// Jacobian's determinant). For a page-to-photo homography: photo pixels per page pixel there.
    /// </summary>
    public static double Scale(double[] m, double x, double y)
    {
        var w = m[6] * x + m[7] * y + m[8];
        var u = (m[0] * x + m[1] * y + m[2]) / w;
        var v = (m[3] * x + m[4] * y + m[5]) / w;
        var dudx = (m[0] - m[6] * u) / w;
        var dudy = (m[1] - m[7] * u) / w;
        var dvdx = (m[3] - m[6] * v) / w;
        var dvdy = (m[4] - m[7] * v) / w;
        return Math.Sqrt(Math.Abs(dudx * dvdy - dudy * dvdx));
    }

    public static Point2d Apply(double[] m, double x, double y)
    {
        var w = m[6] * x + m[7] * y + m[8];
        return new Point2d((m[0] * x + m[1] * y + m[2]) / w, (m[3] * x + m[4] * y + m[5]) / w);
    }
}
