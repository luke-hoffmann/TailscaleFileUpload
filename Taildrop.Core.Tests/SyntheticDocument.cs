using OpenCvSharp;
using Taildrop.Core.Scanning;

namespace Taildrop.Core.Tests;

/// <summary>
/// A synthetic A4 document at 300 dpi and a synthetic phone camera filming it: one frame of the whole page (too few
/// pixels for the small print), close-ups over the page, with optics, exposure, vignetting, noise and JPEG.
/// </summary>
static class SyntheticDocument
{
    /// <summary>The whole page in one frame, slightly tilted, filling most of it.</summary>
    public static (Mat Frame, ScanOutline Outline) WholePage(Mat document, Size frameSize, double gain = 1, bool quarterTurn = false)
    {
        double w = frameSize.Width, h = frameSize.Height;
        var portrait = h > w;
        // As large as fits with a margin: most of the frame's height, and never wider than 88% of it.
        var pageH = Math.Min(portrait ? 0.80 * h : 0.88 * h, 0.88 * w * document.Height / document.Width);
        var pageW = pageH * document.Width / document.Height;
        var cx = w / 2;
        var cy = h / 2;
        Point2d[] corners =
        {
            new(cx - pageW / 2 + 0.02 * pageW, cy - pageH / 2), new(cx + pageW / 2 - 0.01 * pageW, cy - pageH / 2 + 0.012 * pageH),
            new(cx + pageW / 2 + 0.015 * pageW, cy + pageH / 2), new(cx - pageW / 2 - 0.01 * pageW, cy + pageH / 2 - 0.01 * pageH)
        };
        if (quarterTurn)
        {
            // Page turned so its top edge points left: page TL lands at the frame's bottom-left.
            var pw = 0.88 * h * document.Width / document.Height;
            var ph = 0.88 * h;
            corners = new Point2d[]
            {
                new(cx - ph / 2, cy + pw / 2), new(cx - ph / 2, cy - pw / 2), new(cx + ph / 2, cy - pw / 2), new(cx + ph / 2, cy + pw / 2)
            };
        }
        var frame = Render(document, corners, frameSize, gain, seed: 3);
        // The outline as a detector reports it: corners ordered TL TR BR BL as the frame shows them.
        var ordered = DocumentDetector.OrderCorners(corners);
        var outline = ScanOutline.FromCorners(ordered.Select(c => new[] { c.X / w, c.Y / h }).ToArray(), true);
        return (frame, outline);
    }

    /// <summary>Close-ups on a grid over the page, each a little rotated and tilted, at about <paramref name="density"/> camera pixels per page pixel.</summary>
    public static IEnumerable<Mat> CloseUps(Mat document, Size frameSize, int columns, int rows, double density, int seed)
    {
        var rng = new Random(seed);
        var span = frameSize.Width / density;              // page pixels across the frame
        var spanY = frameSize.Height / density;
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < columns; c++)
            {
                // Centres spread edge to edge, so neighbouring close-ups overlap like a sweep over the page does.
                var x = span * 0.4 + (document.Width - span * 0.8) * (columns == 1 ? 0.5 : c / (double)(columns - 1));
                var y = spanY * 0.4 + (document.Height - spanY * 0.8) * (rows == 1 ? 0.5 : r / (double)(rows - 1));
                var angle = (rng.NextDouble() - 0.5) * 0.2;
                var tilt = (rng.NextDouble() - 0.5) * 0.06;
                var gain = 0.85 + rng.NextDouble() * 0.3;
                // Page region seen by the frame, as page-pixel corners, then mapped into the frame.
                var cos = Math.Cos(angle);
                var sin = Math.Sin(angle);
                Point2d Corner(double u, double v) => new(x + (u * cos - v * sin) * span / 2 * (1 + tilt * v), y + (u * sin + v * cos) * spanY / 2);
                var pageCorners = new[] { Corner(-1, -1), Corner(1, -1), Corner(1, 1), Corner(-1, 1) };
                var frameCorners = new[] { new Point2d(0, 0), new Point2d(frameSize.Width, 0), new Point2d(frameSize.Width, frameSize.Height), new Point2d(0, frameSize.Height) };
                var pageToFrame = Geometry.Homography(pageCorners, frameCorners);
                var documentCorners = new[] { new Point2d(0, 0), new Point2d(document.Width, 0), new Point2d(document.Width, document.Height), new Point2d(0, document.Height) }
                    .Select(p => Geometry.Apply(pageToFrame, p.X, p.Y)).ToArray();
                yield return Render(document, documentCorners, frameSize, gain, seed + r * 31 + c);
            }
    }

    /// <summary>
    /// Photographs the document with its corners landing at <paramref name="corners"/> (continuous coordinates: the
    /// frame's top-left pixel spans 0-1, as outlines measure it): optics, exposure, vignetting, noise, JPEG.
    /// </summary>
    public static Mat Render(Mat document, Point2d[] corners, Size frameSize, double gain, int seed)
    {
        var source = new[] { new Point2d(0, 0), new Point2d(document.Width, 0), new Point2d(document.Width, document.Height), new Point2d(0, document.Height) };
        var continuous = Geometry.Homography(source, corners);
        var density = Geometry.Scale(continuous, document.Width / 2.0, document.Height / 2.0);
        // OpenCV warps pixel centres: centre of pixel i is at i, i.e. continuous i + 0.5.
        var toFrame = Geometry.Multiply(new double[] { 1, 0, -0.5, 0, 1, -0.5, 0, 0, 1 }, Geometry.Multiply(continuous, new double[] { 1, 0, 0.5, 0, 1, 0.5, 0, 0, 1 }));

        // A sensor integrates light over its pixels: shrinking the page without blurring first would alias.
        using var filtered = new Mat();
        if (density < 1) Cv2.GaussianBlur(document, filtered, new Size(0, 0), 0.45 / density);
        else document.CopyTo(filtered);

        var frame = new Mat();
        using (var transform = new Mat(3, 3, MatType.CV_64FC1))
        {
            for (var i = 0; i < 9; i++) transform.Set(i / 3, i % 3, toFrame[i]);
            Cv2.WarpPerspective(filtered, frame, transform, frameSize, InterpolationFlags.Linear, BorderTypes.Constant, new Scalar(52, 70, 92));
        }
        Cv2.GaussianBlur(frame, frame, new Size(0, 0), 0.7);

        // Exposure and a soft vignette.
        using var light3 = new Mat();
        Cv2.Multiply(Vignette(frameSize), Scalar.All(gain), light3);
        using var frame32 = new Mat();
        frame.ConvertTo(frame32, MatType.CV_32FC3);
        Cv2.Multiply(frame32, light3, frame32);
        using var noise = new Mat(frameSize, MatType.CV_32FC3);
        Cv2.SetTheRNG((ulong)(seed * 7919 + 13));
        Cv2.Randn(noise, Scalar.All(0), Scalar.All(2.5));
        Cv2.Add(frame32, noise, frame32);
        frame32.ConvertTo(frame, MatType.CV_8UC3);

        Cv2.ImEncode(".jpg", frame, out var jpeg, new ImageEncodingParam(ImwriteFlags.JpegQuality, 90));
        frame.Dispose();
        return Cv2.ImDecode(jpeg, ImreadModes.Color);
    }

    static readonly Dictionary<(int, int), Mat> Vignettes = new();

    /// <summary>Light falling off toward the frame's corners (3 channels, 1.0 in the middle).</summary>
    static Mat Vignette(Size size)
    {
        lock (Vignettes)
        {
            if (Vignettes.TryGetValue((size.Width, size.Height), out var cached)) return cached;
            var values = new float[size.Width * size.Height];
            for (var y = 0; y < size.Height; y++)
                for (var x = 0; x < size.Width; x++)
                {
                    var dx = (x - size.Width / 2.0) / size.Width;
                    var dy = (y - size.Height / 2.0) / size.Height;
                    values[y * size.Width + x] = (float)(1 - 0.35 * (dx * dx + dy * dy));
                }
            using var light = new Mat(size, MatType.CV_32FC1);
            for (var y = 0; y < size.Height; y++) System.Runtime.InteropServices.Marshal.Copy(values, y * size.Width, light.Ptr(y), size.Width);
            var light3 = new Mat();
            Cv2.Merge(new[] { light, light, light }, light3);
            Vignettes[(size.Width, size.Height)] = light3;
            return light3;
        }
    }

    /// <summary>A page of small print: headings, paragraphs, a table rule, a picture, and blank margins.</summary>
    public static Mat Make(int width, int height, int seed)
    {
        var rng = new Random(seed);
        var page = new Mat(height, width, MatType.CV_8UC3, new Scalar(238, 242, 244));
        string[] words = { "invoice", "total", "amount", "the", "of", "payment", "due", "account", "number", "date", "reference", "service", "quantity", "unit", "price", "tax", "balance", "customer", "order", "delivery", "address", "terms", "net", "thirty", "days", "item", "description", "period", "rate", "sum" };
        var margin = 200;
        var y = margin + 60;
        Cv2.PutText(page, $"STATEMENT {rng.Next(1000, 9999)}", new Point(margin, y), HersheyFonts.HersheyDuplex, 2.4, new Scalar(120, 60, 20), 5, LineTypes.AntiAlias);
        y += 110;
        while (y < height - margin)
        {
            if (rng.NextDouble() < 0.12)
            {
                y += 70; // paragraph gap: a band of bare paper
                continue;
            }
            if (rng.NextDouble() < 0.05 && y < height - margin - 420)
            {
                // A picture: soft colored blobs.
                var box = new Rect(margin + rng.Next(0, 900), y, 700, 380);
                Cv2.Rectangle(page, box, new Scalar(200, 170, 150), -1);
                for (var k = 0; k < 6; k++)
                    Cv2.Circle(page, new Point(box.X + rng.Next(60, 640), box.Y + rng.Next(60, 320)), rng.Next(30, 110), new Scalar(rng.Next(40, 200), rng.Next(60, 200), rng.Next(80, 230)), -1, LineTypes.AntiAlias);
                y += 420;
                continue;
            }
            var x = margin;
            while (true)
            {
                var word = words[rng.Next(words.Length)];
                if (rng.NextDouble() < 0.15) word = rng.Next(10, 99999).ToString();
                var size = Cv2.GetTextSize(word, HersheyFonts.HersheySimplex, 0.75, 2, out _);
                if (x + size.Width > width - margin) break;
                Cv2.PutText(page, word, new Point(x, y), HersheyFonts.HersheySimplex, 0.75, new Scalar(30, 28, 26), 2, LineTypes.AntiAlias);
                x += size.Width + 14;
            }
            if (rng.NextDouble() < 0.08) Cv2.Line(page, new Point(margin, y + 14), new Point(width - margin, y + 14), new Scalar(90, 90, 90), 2, LineTypes.AntiAlias);
            y += 38;
        }
        return page;
    }
}
