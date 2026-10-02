using OpenCvSharp;

namespace Taildrop.Core.Scanning;

public enum ScanFilter
{
    /// <summary>Even lighting, white paper, true colors.</summary>
    Auto,
    Gray,
    BlackWhite,
    /// <summary>The flattened page exactly as photographed.</summary>
    Original
}

/// <summary>Document-style clean-up of a flattened page: even out shading, whiten paper, sharpen print.</summary>
public static class ScanEnhancer
{
    public static Mat Apply(Mat flat, ScanFilter filter)
    {
        switch (filter)
        {
            case ScanFilter.Original:
                return flat.Clone();
            case ScanFilter.Auto:
                return Normalize(flat);
            case ScanFilter.Gray:
            {
                using var normalized = Normalize(flat);
                return normalized.CvtColor(ColorConversionCodes.BGR2GRAY);
            }
            default:
            {
                using var normalized = Normalize(flat);
                using var gray = normalized.CvtColor(ColorConversionCodes.BGR2GRAY);
                var shortSide = Math.Min(gray.Width, gray.Height);
                var block = Math.Clamp(shortSide / 25, 25, 101) | 1;
                var bw = new Mat();
                Cv2.AdaptiveThreshold(gray, bw, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.Binary, block, 12);
                return bw;
            }
        }
    }

    /// <summary>
    /// Divides by an estimate of the paper's own brightness (a heavily blurred copy with the print "erased"
    /// by a max filter), which removes shadows and lighting gradients and white-balances the paper, then
    /// stretches the blacks and sharpens a touch.
    /// </summary>
    static Mat Normalize(Mat flat)
    {
        var sw = Math.Max(16, flat.Width / 8);
        var sh = Math.Max(16, flat.Height / 8);
        using var small = new Mat();
        Cv2.Resize(flat, small, new Size(sw, sh), 0, 0, InterpolationFlags.Area);

        var kernelSize = Math.Clamp(Math.Min(sw, sh) / 18, 7, 25) | 1;
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(kernelSize, kernelSize));
        using var paper = new Mat();
        Cv2.Dilate(small, paper, kernel);
        Cv2.GaussianBlur(paper, paper, new Size(0, 0), kernelSize / 2.0);

        // Don't let big dark areas (a photo, a solid logo) be "corrected" away: the paper is never
        // darker than about half of its typical brightness, so anything darker than that stays dark.
        using var paperGray = paper.CvtColor(ColorConversionCodes.BGR2GRAY);
        var typical = Percentile(paperGray, 0.95);
        var floor = Math.Max(60, typical * 0.55);
        Cv2.Max(paper, Scalar.All(floor), paper);

        using var paperFull = new Mat();
        Cv2.Resize(paper, paperFull, flat.Size(), 0, 0, InterpolationFlags.Linear);
        var normalized = new Mat();
        Cv2.Divide(flat, paperFull, normalized, 255);

        // Stretch the darkest print towards black (never past what the page itself contains).
        using var gray = new Mat();
        using var graySmall = new Mat();
        Cv2.Resize(normalized, graySmall, new Size(Math.Max(16, flat.Width / 4), Math.Max(16, flat.Height / 4)), 0, 0, InterpolationFlags.Area);
        Cv2.CvtColor(graySmall, gray, ColorConversionCodes.BGR2GRAY);
        var black = Math.Min(Percentile(gray, 0.005), 80);
        if (black > 10)
        {
            var alpha = 255.0 / (255 - black);
            normalized.ConvertTo(normalized, -1, alpha, -black * alpha);
        }

        // Mild unsharp mask to win back what resampling softened.
        using var blurred = new Mat();
        Cv2.GaussianBlur(normalized, blurred, new Size(0, 0), 1.2);
        Cv2.AddWeighted(normalized, 1.5, blurred, -0.5, 0, normalized);
        return normalized;
    }

    /// <summary>Brightness at the given fraction (0-1) of an 8-bit single-channel image.</summary>
    static double Percentile(Mat gray8, double fraction)
    {
        using var histogram = new Mat();
        Cv2.CalcHist(new[] { gray8 }, new[] { 0 }, null, histogram, 1, new[] { 256 }, new[] { new Rangef(0, 256) });
        var total = gray8.Rows * (double)gray8.Cols;
        double running = 0;
        for (var i = 0; i < 256; i++)
        {
            running += histogram.At<float>(i);
            if (running >= fraction * total) return i;
        }
        return 255;
    }
}
