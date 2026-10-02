namespace Taildrop.Core.Scanning;

/// <summary>
/// The outline of a page in a photo: four corners plus the four edges between them, so a bent or
/// curled page can be flattened instead of just cropped. All coordinates are normalized (0-1) to the
/// EXIF-oriented photo, [x, y]. Corners are ordered TL, TR, BR, BL.
/// Edge runs share the corner endpoints and are all stored in reading direction:
/// <c>Top</c> TL→TR, <c>Right</c> TR→BR, <c>Bottom</c> BL→BR, <c>Left</c> TL→BL.
/// </summary>
public sealed class ScanOutline
{
    public const int EdgeSamples = 33;

    public double[][] Corners { get; set; } = Array.Empty<double[]>();
    public double[][] Top { get; set; } = Array.Empty<double[]>();
    public double[][] Right { get; set; } = Array.Empty<double[]>();
    public double[][] Bottom { get; set; } = Array.Empty<double[]>();
    public double[][] Left { get; set; } = Array.Empty<double[]>();
    public bool Confident { get; set; }

    /// <summary>Straight-edged outline through four corners (TL, TR, BR, BL).</summary>
    public static ScanOutline FromCorners(double[][] corners, bool confident)
    {
        var outline = new ScanOutline { Corners = corners, Confident = confident };
        outline.StraightenEdges();
        return outline;
    }

    /// <summary>Full frame with a small inset, used when no page could be found.</summary>
    public static ScanOutline FullFrame() => FromCorners(new[]
    {
        new[] { 0.03, 0.03 }, new[] { 0.97, 0.03 }, new[] { 0.97, 0.97 }, new[] { 0.03, 0.97 }
    }, confident: false);

    /// <summary>Replace all four edges by straight lines between the corners.</summary>
    public void StraightenEdges()
    {
        Top = Line(Corners[0], Corners[1]);
        Right = Line(Corners[1], Corners[2]);
        Bottom = Line(Corners[3], Corners[2]);
        Left = Line(Corners[0], Corners[3]);
    }

    static double[][] Line(double[] a, double[] b)
    {
        var points = new double[EdgeSamples][];
        for (var i = 0; i < EdgeSamples; i++)
        {
            var t = i / (double)(EdgeSamples - 1);
            points[i] = new[] { a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t };
        }
        return points;
    }

    /// <summary>
    /// Validates an outline that arrived over the network and makes it self-consistent: every number
    /// finite and near the frame, every edge exactly <see cref="EdgeSamples"/> points, and the edge
    /// endpoints pinned to the corners. Returns null when it cannot be repaired.
    /// </summary>
    public static ScanOutline? Sanitize(ScanOutline? input)
    {
        if (input is null || input.Corners.Length != 4) return null;
        var corners = new double[4][];
        for (var i = 0; i < 4; i++)
        {
            if (!ValidPoint(input.Corners[i])) return null;
            corners[i] = new[] { input.Corners[i][0], input.Corners[i][1] };
        }

        var outline = new ScanOutline { Corners = corners, Confident = true };
        outline.Top = CleanEdge(input.Top, corners[0], corners[1]);
        outline.Right = CleanEdge(input.Right, corners[1], corners[2]);
        outline.Bottom = CleanEdge(input.Bottom, corners[3], corners[2]);
        outline.Left = CleanEdge(input.Left, corners[0], corners[3]);
        return outline.Area() > 0.0004 ? outline : null;
    }

    static bool ValidPoint(double[]? p) =>
        p is { Length: 2 } && double.IsFinite(p[0]) && double.IsFinite(p[1]) && p[0] is > -0.5 and < 1.5 && p[1] is > -0.5 and < 1.5;

    static double[][] CleanEdge(double[][]? edge, double[] start, double[] end)
    {
        if (edge is null || edge.Length < 2 || edge.Length > 257 || edge.Any(p => !ValidPoint(p)))
            return Line(start, end);

        // Resample onto the standard sample count (by index) and pin the ends to the corners.
        var result = new double[EdgeSamples][];
        for (var i = 0; i < EdgeSamples; i++)
        {
            var position = i / (double)(EdgeSamples - 1) * (edge.Length - 1);
            var lo = (int)Math.Floor(position);
            var hi = Math.Min(lo + 1, edge.Length - 1);
            var f = position - lo;
            result[i] = new[]
            {
                edge[lo][0] + (edge[hi][0] - edge[lo][0]) * f,
                edge[lo][1] + (edge[hi][1] - edge[lo][1]) * f
            };
        }
        result[0] = new[] { start[0], start[1] };
        result[EdgeSamples - 1] = new[] { end[0], end[1] };
        return result;
    }

    /// <summary>Shoelace area of the corner quad, in normalized units.</summary>
    public double Area()
    {
        double sum = 0;
        for (var i = 0; i < 4; i++)
        {
            var a = Corners[i];
            var b = Corners[(i + 1) % 4];
            sum += a[0] * b[1] - b[0] * a[1];
        }
        return Math.Abs(sum) / 2;
    }
}
