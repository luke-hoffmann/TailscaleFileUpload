using OpenCvSharp;

namespace Taildrop.Core.Scanning;

/// <summary>
/// Traces the real (possibly curved) paper edge along each side of a page quadrilateral, at higher resolution
/// than detection. Dynamic programming picks, for every sample along the side, how far in or out the edge is:
/// it is rewarded for sitting on a paper-to-background step and penalised for moving away from the straight
/// side and - quadratically - for sudden jumps. Paper bends gradually, so curls and bowed edges are followed;
/// another sheet's edge is a sudden jump away, so a packet or a tab poking out is not.
/// </summary>
static class EdgeTracer
{
    const int WorkEdge = 1800;
    const int Samples = 64;

    /// <summary>Corners are TL, TR, BR, BL in full-image pixels. Returns the outline (normalized).</summary>
    public static ScanOutline Trace(Mat image, Point2d[] corners, bool confident)
    {
        var scale = Math.Min(1.0, WorkEdge / (double)Math.Max(image.Width, image.Height));
        using var work = new Mat();
        if (scale < 1) Cv2.Resize(image, work, new Size(), scale, scale, InterpolationFlags.Area);
        else image.CopyTo(work);
        using var gray = work.CvtColor(ColorConversionCodes.BGR2GRAY);
        Cv2.GaussianBlur(gray, gray, new Size(0, 0), 1.0);
        using var gx = new Mat();
        using var gy = new Mat();
        Cv2.Sobel(gray, gx, MatType.CV_32F, 1, 0, 3, 0.25);
        Cv2.Sobel(gray, gy, MatType.CV_32F, 0, 1, 3, 0.25);

        var q = corners.Select(c => new Point2d(c.X * scale, c.Y * scale)).ToArray();
        var cx = q.Average(p => p.X);
        var cy = q.Average(p => p.Y);
        var polarity = Polarity(gray, q, cx, cy);

        var sides = new Point2d[4][];
        for (var s = 0; s < 4; s++) sides[s] = TraceSide(gray, gx, gy, q[s], q[(s + 1) % 4], cx, cy, polarity);

        // Corners where the traced edges actually meet: each side's end is followed by a line fitted a little way
        // in from the corner. A bulging side then leads to the real corner, not to where its middle points; a
        // folded-over corner leaves both sides running straight on, so the corner stays the unfolded one.
        var diag = Geometry.Distance(q[0], q[2]);
        for (var c = 0; c < 4; c++)
        {
            var incoming = sides[(c + 3) % 4];
            var outgoing = sides[c];
            var a = EndLine(incoming, atStart: false);
            var b = EndLine(outgoing, atStart: true);
            var p = Intersect(a, b);
            if (p is null || Geometry.Distance(p.Value, q[c]) > MaxCornerShift * diag) continue;
            q[c] = p.Value;
        }
        for (var s = 0; s < 4; s++)
        {
            sides[s][0] = q[s];
            sides[s][^1] = q[(s + 1) % 4];
        }

        double[] N(Point2d p) => new[] { p.X / work.Width, p.Y / work.Height };
        Point2d[] R(Point2d[] run) => Geometry.ResampleByArcLength(run, ScanOutline.EdgeSamples);
        return new ScanOutline
        {
            Corners = q.Select(N).ToArray(),
            Top = R(sides[0]).Select(N).ToArray(),                       // TL -> TR
            Right = R(sides[1]).Select(N).ToArray(),                     // TR -> BR
            Bottom = R(sides[2]).Reverse().Select(N).ToArray(),          // BR -> BL, stored BL -> BR
            Left = R(sides[3]).Reverse().Select(N).ToArray(),            // BL -> TL, stored TL -> BL
            Confident = confident
        };
    }

    /// <summary>+1 when the paper is brighter than its surroundings (the usual case), -1 otherwise.</summary>
    static int Polarity(Mat gray, Point2d[] q, double cx, double cy)
    {
        double inside = 0, outside = 0;
        for (var s = 0; s < 4; s++)
        {
            var a = q[s];
            var b = q[(s + 1) % 4];
            var (nx, ny) = OutwardNormal(a, b, cx, cy);
            for (var i = 1; i < 20; i++)
            {
                var t = i / 20.0;
                var x = a.X + (b.X - a.X) * t;
                var y = a.Y + (b.Y - a.Y) * t;
                inside += Sample(gray, x - nx * 8, y - ny * 8);
                outside += Sample(gray, x + nx * 8, y + ny * 8);
            }
        }
        return inside >= outside ? 1 : -1;
    }

    static Point2d[] TraceSide(Mat gray, Mat gx, Mat gy, Point2d a, Point2d b, double cx, double cy, int polarity)
    {
        var length = Geometry.Distance(a, b);
        var (nx, ny) = OutwardNormal(a, b, cx, cy);
        var outMax = (int)Math.Max(8, 0.075 * length);
        var inMax = (int)Math.Max(6, 0.035 * length);
        var m = outMax + inMax + 1;
        var k = Samples + 1;

        // Unary cost: reward a paper -> background step along the outward normal, mild pull towards the straight side.
        var unary = new double[k, m];
        for (var i = 0; i < k; i++)
        {
            var t = i / (double)Samples;
            var x0 = a.X + (b.X - a.X) * t;
            var y0 = a.Y + (b.Y - a.Y) * t;
            for (var j = 0; j < m; j++)
            {
                var o = j - inMax;
                var x = x0 + nx * o;
                var y = y0 + ny * o;
                double reward = 0;
                if (x >= 1 && y >= 1 && x < gray.Width - 2 && y < gray.Height - 2)
                {
                    var d = Bilinear(gx, x, y) * nx + Bilinear(gy, x, y) * ny;   // brightness change going outward
                    reward = Math.Clamp(-polarity * d, 0, 30) / 30.0;
                }
                unary[i, j] = -reward + 0.35 * Math.Abs(o) / Math.Max(outMax, inMax);
            }
        }

        // Ends are pinned to the corners.
        const double jump = 0.02;      // quadratic: gradual bends are cheap, sudden jumps (another sheet's edge) are not
        const double step = 0.04;
        var cost = new double[k, m];
        var from = new int[k, m];
        for (var j = 0; j < m; j++) cost[0, j] = unary[0, j] + EndFreedom * Math.Abs(j - inMax);
        for (var i = 1; i < k; i++)
        for (var j = 0; j < m; j++)
        {
            var best = double.MaxValue;
            var arg = 0;
            var lo = Math.Max(0, j - 12);
            var hi = Math.Min(m - 1, j + 12);
            for (var p = lo; p <= hi; p++)
            {
                var dj = j - p;
                var c = cost[i - 1, p] + step * Math.Abs(dj) + jump * dj * dj;
                if (c < best) { best = c; arg = p; }
            }
            cost[i, j] = best + unary[i, j] + (i == k - 1 ? EndFreedom * Math.Abs(j - inMax) : 0);
            from[i, j] = arg;
        }

        var offsets = new int[k];
        var last = 0;
        for (var j = 1; j < m; j++) if (cost[k - 1, j] < cost[k - 1, last]) last = j;
        offsets[k - 1] = last;
        for (var i = k - 1; i > 0; i--) offsets[i - 1] = from[i, offsets[i]];

        var points = new Point2d[k];
        for (var i = 0; i < k; i++)
        {
            var t = i / (double)Samples;
            var o = offsets[i] - inMax;
            points[i] = new Point2d(a.X + (b.X - a.X) * t + nx * o, a.Y + (b.Y - a.Y) * t + ny * o);
        }
        Geometry.SmoothInPlace(points, 2);
        return points;
    }

    internal static double EndFreedom = 0.02, MaxCornerShift = 0.04;

    /// <summary>Line through the traced points 8-25% of the way in from one end (point, direction).</summary>
    static (Point2d P, Point2d D) EndLine(Point2d[] run, bool atStart)
    {
        var n = run.Length;
        var pts = new List<Point2f>();
        for (var i = (int)(n * 0.08); i <= (int)(n * 0.25); i++)
        {
            var p = run[atStart ? i : n - 1 - i];
            pts.Add(new Point2f((float)p.X, (float)p.Y));
        }
        var line = Cv2.FitLine(pts, DistanceTypes.L2, 0, 0.01, 0.01);
        return (new Point2d(line.X1, line.Y1), new Point2d(line.Vx, line.Vy));
    }

    static Point2d? Intersect((Point2d P, Point2d D) a, (Point2d P, Point2d D) b)
    {
        var den = a.D.X * b.D.Y - a.D.Y * b.D.X;
        if (Math.Abs(den) < 0.2) return null;   // nearly parallel: no reliable corner
        var t = ((b.P.X - a.P.X) * b.D.Y - (b.P.Y - a.P.Y) * b.D.X) / den;
        return new Point2d(a.P.X + a.D.X * t, a.P.Y + a.D.Y * t);
    }

    static (double X, double Y) OutwardNormal(Point2d a, Point2d b, double cx, double cy)
    {
        var len = Geometry.Distance(a, b);
        var nx = (b.Y - a.Y) / len;
        var ny = -(b.X - a.X) / len;
        if (((a.X + b.X) / 2 - cx) * nx + ((a.Y + b.Y) / 2 - cy) * ny < 0) { nx = -nx; ny = -ny; }
        return (nx, ny);
    }

    static double Sample(Mat gray, double x, double y)
    {
        var xi = Math.Clamp((int)Math.Round(x), 0, gray.Width - 1);
        var yi = Math.Clamp((int)Math.Round(y), 0, gray.Height - 1);
        return gray.At<byte>(yi, xi);
    }

    static double Bilinear(Mat m, double x, double y)
    {
        var x0 = (int)x;
        var y0 = (int)y;
        var fx = x - x0;
        var fy = y - y0;
        var v00 = m.At<float>(y0, x0);
        var v10 = m.At<float>(y0, x0 + 1);
        var v01 = m.At<float>(y0 + 1, x0);
        var v11 = m.At<float>(y0 + 1, x0 + 1);
        return (v00 * (1 - fx) + v10 * fx) * (1 - fy) + (v01 * (1 - fx) + v11 * fx) * fy;
    }
}
