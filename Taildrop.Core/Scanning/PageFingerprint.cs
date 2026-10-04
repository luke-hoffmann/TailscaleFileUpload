using OpenCvSharp;

namespace Taildrop.Core.Scanning;

/// <summary>
/// A tiny, lighting-independent thumbnail of what is printed on a page, so the live scanner can tell the page it
/// just scanned from the next one even when the next one lies in exactly the same spot. 32 x 32 gray, zero mean,
/// unit variance, compared in all four rotations (the phone may be turned between pages).
/// </summary>
public static class PageFingerprint
{
    const int Side = 32;
    /// <summary>Normalized correlation above which two fingerprints are the same page.</summary>
    const double SameAbove = 0.82;
    /// <summary>Gray-level spread below which a page is blank as far as a fingerprint can tell. Blank is never "the same".</summary>
    const double MinContrast = 3;
    /// <summary>Outlines err outward; the outer margin may show a sliver of table, so it is left out.</summary>
    const double Inset = 0.06;

    /// <summary>Fingerprint of the page inside <paramref name="corners"/> (normalized TL TR BR BL) of a camera frame.</summary>
    public static float[]? FromFrame(Mat frame, double[][] corners)
    {
        const int Work = 128;
        var source = corners.Select(c => new Point2f((float)(c[0] * frame.Width), (float)(c[1] * frame.Height))).ToArray();
        var target = new[] { new Point2f(0, 0), new Point2f(Work, 0), new Point2f(Work, Work), new Point2f(0, Work) };
        using var transform = Cv2.GetPerspectiveTransform(source, target);
        using var warped = new Mat();
        Cv2.WarpPerspective(frame, warped, transform, new Size(Work, Work), InterpolationFlags.Area, BorderTypes.Replicate);
        return FromPage(warped);
    }

    /// <summary>Fingerprint of a flat, upright page image.</summary>
    public static float[]? FromPage(Mat page)
    {
        var x = (int)Math.Round(page.Width * Inset);
        var y = (int)Math.Round(page.Height * Inset);
        using var inner = new Mat(page, new Rect(x, y, page.Width - 2 * x, page.Height - 2 * y));
        using var gray = inner.Channels() == 1 ? inner.Clone() : inner.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var small = new Mat();
        Cv2.Resize(gray, small, new Size(Side, Side), 0, 0, InterpolationFlags.Area);

        var values = new float[Side * Side];
        for (var row = 0; row < Side; row++)
            for (var col = 0; col < Side; col++)
                values[row * Side + col] = small.At<byte>(row, col);

        var mean = values.Average();
        var spread = Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / values.Length);
        if (spread < MinContrast) return null;
        for (var i = 0; i < values.Length; i++) values[i] = (float)((values[i] - mean) / spread);
        return values;
    }

    /// <summary>True when both fingerprints exist and show the same page, in any of the four rotations.</summary>
    public static bool Same(float[]? a, float[]? b)
    {
        if (a is null || b is null || a.Length != Side * Side || b.Length != Side * Side) return false;
        for (var rotation = 0; rotation < 4; rotation++)
        {
            double sum = 0;
            for (var row = 0; row < Side; row++)
                for (var col = 0; col < Side; col++)
                {
                    var (r, c) = rotation switch
                    {
                        0 => (row, col),
                        1 => (Side - 1 - col, row),
                        2 => (Side - 1 - row, Side - 1 - col),
                        _ => (col, Side - 1 - row)
                    };
                    sum += a[row * Side + col] * b[r * Side + c];
                }
            if (sum / (Side * Side) > SameAbove) return true;
        }
        return false;
    }
}
