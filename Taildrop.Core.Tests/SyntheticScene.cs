using OpenCvSharp;
using Taildrop.Core.Scanning;

namespace Taildrop.Core.Tests;

public enum Backdrop { DarkWood, BlueCloth, TanWood, Granite, LightDesk, WhiteDesk }

public sealed class SceneOptions
{
    public int Width = 1600;
    public int Height = 1200;
    public double PageMmWidth = 210;
    public double PageMmHeight = 297;
    public double TiltDeg = 20;
    public double YawDeg = 12;
    public double RollDeg = 6;
    /// <summary>Roughly how much of the frame height the page spans.</summary>
    public double Fill = 0.72;
    public double FocalFactor = 0.75;
    public Backdrop Backdrop = Backdrop.DarkWood;
    /// <summary>Every row of the page is shifted sideways by a sine of its height (an S-shaped, snaking paper).</summary>
    public double SnakePx;
    /// <summary>Left/right edges bow outward by this many pixels at mid-height.</summary>
    public double PillowXPx;
    /// <summary>Top/bottom edges bow outward by this many pixels at mid-width.</summary>
    public double PillowYPx;
    public bool Lighting = true;
    public int Seed = 7;
    /// <summary>0-3: another (cream) sheet lies under that page corner (TL, TR, BR, BL) and runs off, like the
    /// folded-back tab of a packet. -1: none.</summary>
    public int TabCorner = -1;
    /// <summary>Top-left corner folded over along a 45° line this many mm from the corner (0: none). The table shows
    /// where the corner was; the blank back of the flap lies on the page.</summary>
    public double DogEarMm;
    /// <summary>Three binder holes along the left edge (the table shows through).</summary>
    public bool HolePunches;
}

/// <summary>
/// A synthetic photo of a page: perspective from a pinhole camera, optional bend, a textured backdrop,
/// uneven lighting and sensor noise, plus the exact ground truth (outline, mask, aspect).
/// The page carries four red corner markers and four straight horizontal reference bars.
/// </summary>
sealed class SyntheticScene : IDisposable
{
    public const int TexelsPerMm = 4;
    public static readonly double[] BarFractions = { 0.2, 0.4, 0.6, 0.8 };
    public const int MarkerCenterInset = 23; // texels from each page corner to the marker centre

    public Mat Photo { get; }
    /// <summary>255 where the page is, 0 elsewhere (before blur).</summary>
    public Mat TruthMask { get; }
    public ScanOutline TruthOutline { get; }
    public double TrueAspect { get; }
    public int PageTexelsWide { get; }
    public int PageTexelsHigh { get; }
    public SceneOptions Options { get; }

    public SyntheticScene(SceneOptions options)
    {
        Options = options;
        var rng = new Random(options.Seed);
        // OpenCV's noise generator is thread-local and shared; pin it so every scene is reproducible.
        Cv2.SetTheRNG((ulong)(options.Seed * 2654435761L + 1));
        PageTexelsWide = (int)(options.PageMmWidth * TexelsPerMm);
        PageTexelsHigh = (int)(options.PageMmHeight * TexelsPerMm);
        TrueAspect = options.PageMmWidth / options.PageMmHeight;

        using var texture = MakePageTexture(PageTexelsWide, PageTexelsHigh, rng);

        // Pinhole camera: page centred, rotated by roll/yaw/tilt, pushed away so it fills the frame.
        var f = options.FocalFactor * Math.Max(options.Width, options.Height);
        var distance = f * options.PageMmHeight / (options.Fill * options.Height);
        var corners3d = new[]
        {
            (-options.PageMmWidth / 2, -options.PageMmHeight / 2), (options.PageMmWidth / 2, -options.PageMmHeight / 2),
            (options.PageMmWidth / 2, options.PageMmHeight / 2), (-options.PageMmWidth / 2, options.PageMmHeight / 2)
        };
        var projected = corners3d.Select(c => Project(c.Item1, c.Item2, f, distance, options)).ToArray();
        var toImage = Geometry.Homography(new[]
        {
            new Point2d(0, 0), new Point2d(PageTexelsWide, 0), new Point2d(PageTexelsWide, PageTexelsHigh), new Point2d(0, PageTexelsHigh)
        }, projected);
        var toPage = Geometry.Invert(toImage);

        // Truth outline: page edges after the bend is applied.
        TruthOutline = BuildTruthOutline(toImage, options);

        // Render: for every photo pixel find which page point lands there (undoing the bend iteratively).
        var width = options.Width;
        var height = options.Height;
        using var mapX = new Mat(height, width, MatType.CV_32FC1);
        using var mapY = new Mat(height, width, MatType.CV_32FC1);
        TruthMask = new Mat(height, width, MatType.CV_8UC1, Scalar.All(0));
        var xs = new float[height][];
        var ys = new float[height][];
        var inside = new byte[height][];
        var fold = new byte[height][];   // 1 = corner folded away (table shows), 2 = flap (back of the paper)
        var d = options.DogEarMm * TexelsPerMm;
        Parallel.For(0, height, y =>
        {
            var rowX = new float[width];
            var rowY = new float[width];
            var rowInside = new byte[width];
            var rowFold = new byte[width];
            for (var x = 0; x < width; x++)
            {
                double px = x, py = y;
                var page = Geometry.Apply(toPage, px, py);
                for (var iteration = 0; iteration < 6; iteration++)
                {
                    var (dx, dy) = Bend(page.X / PageTexelsWide, page.Y / PageTexelsHigh, options);
                    page = Geometry.Apply(toPage, x - dx, y - dy);
                }
                rowX[x] = (float)page.X;
                rowY[x] = (float)page.Y;
                rowInside[x] = page.X >= 0 && page.X <= PageTexelsWide && page.Y >= 0 && page.Y <= PageTexelsHigh ? (byte)255 : (byte)0;
                if (options.HolePunches && rowInside[x] != 0)
                {
                    var hx = page.X / TexelsPerMm - 10;
                    foreach (var hy in new[] { 0.2, 0.5, 0.8 })
                    {
                        var dy = page.Y / TexelsPerMm - hy * options.PageMmHeight;
                        if (hx * hx + dy * dy < 3.2 * 3.2) rowFold[x] = 1;
                    }
                }
                if (d > 0 && rowInside[x] != 0)
                {
                    if (page.X + page.Y < d) rowFold[x] = 1;
                    else if (page.X < d && page.Y < d) rowFold[x] = 2;   // mirror of the corner across the fold line
                }
            }
            xs[y] = rowX; ys[y] = rowY; inside[y] = rowInside; fold[y] = rowFold;
        });
        for (var y = 0; y < height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(xs[y], 0, mapX.Ptr(y), width);
            System.Runtime.InteropServices.Marshal.Copy(ys[y], 0, mapY.Ptr(y), width);
            System.Runtime.InteropServices.Marshal.Copy(inside[y], 0, TruthMask.Ptr(y), width);
        }

        using var page2 = new Mat();
        Cv2.Remap(texture, page2, mapX, mapY, InterpolationFlags.Linear, BorderTypes.Replicate);
        using var backdrop = MakeBackdrop(width, height, options.Backdrop, rng);
        if (options.TabCorner is >= 0 and < 4)
        {
            var (sx, sy) = new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) }[options.TabCorner];
            double tw = options.PageMmWidth * 0.85, th = options.PageMmHeight * 0.7;
            var cxMm = sx * (options.PageMmWidth / 2 + tw / 2 - 0.3 * tw);
            var cyMm = sy * (options.PageMmHeight / 2 + th / 2 - 0.3 * th);
            var angle = 14 * Math.PI / 180 * sx;
            var tab = new[] { (-tw / 2, -th / 2), (tw / 2, -th / 2), (tw / 2, th / 2), (-tw / 2, th / 2) }
                .Select(c => Project(cxMm + c.Item1 * Math.Cos(angle) - c.Item2 * Math.Sin(angle), cyMm + c.Item1 * Math.Sin(angle) + c.Item2 * Math.Cos(angle), f, distance, options))
                .Select(p => new Point((int)Math.Round(p.X), (int)Math.Round(p.Y))).ToArray();
            Cv2.FillConvexPoly(backdrop, tab, new Scalar(205, 228, 240), LineTypes.AntiAlias);
        }
        var photo = backdrop.Clone();
        page2.CopyTo(photo, TruthMask);
        if (d > 0 || options.HolePunches)
        {
            var flap = new Vec3b(222, 226, 228);
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                if (fold[y][x] == 1) photo.Set(y, x, backdrop.At<Vec3b>(y, x));
                else if (fold[y][x] == 2) photo.Set(y, x, flap);
            }
        }

        if (options.Lighting) ApplyLighting(photo, rng);
        Cv2.GaussianBlur(photo, photo, new Size(3, 3), 0.8);
        Photo = photo;
    }

    static Point2d Project(double x, double y, double f, double distance, SceneOptions o)
    {
        // Rotate the page plane (roll about z, yaw about y, tilt about x), then translate away from the camera.
        double rx = o.TiltDeg * Math.PI / 180, ry = o.YawDeg * Math.PI / 180, rz = o.RollDeg * Math.PI / 180;
        double px = x, py = y, pz = 0;
        // tilt (about x)
        (py, pz) = (py * Math.Cos(rx) - pz * Math.Sin(rx), py * Math.Sin(rx) + pz * Math.Cos(rx));
        // yaw (about y)
        (px, pz) = (px * Math.Cos(ry) + pz * Math.Sin(ry), -px * Math.Sin(ry) + pz * Math.Cos(ry));
        // roll (about z)
        (px, py) = (px * Math.Cos(rz) - py * Math.Sin(rz), px * Math.Sin(rz) + py * Math.Cos(rz));
        pz += distance;
        return new Point2d(o.Width / 2.0 + f * px / pz, o.Height / 2.0 + f * py / pz);
    }

    /// <summary>Displacement (photo pixels) of page point (a, b), both 0-1.</summary>
    static (double X, double Y) Bend(double a, double b, SceneOptions o)
    {
        var dx = o.SnakePx * Math.Sin(2 * Math.PI * b) + o.PillowXPx * Math.Sin(Math.PI * b) * (2 * a - 1);
        var dy = o.PillowYPx * Math.Sin(Math.PI * a) * (2 * b - 1);
        return (dx, dy);
    }

    ScanOutline BuildTruthOutline(double[] toImage, SceneOptions o)
    {
        Point2d At(double a, double b)
        {
            var p = Geometry.Apply(toImage, a * PageTexelsWide, b * PageTexelsHigh);
            var (dx, dy) = Bend(a, b, o);
            return new Point2d((p.X + dx) / o.Width, (p.Y + dy) / o.Height);
        }

        double[][] Edge(Func<double, Point2d> at) => Enumerable.Range(0, ScanOutline.EdgeSamples)
            .Select(i => at(i / (double)(ScanOutline.EdgeSamples - 1)))
            .Select(p => new[] { p.X, p.Y }).ToArray();

        var corners = new[] { At(0, 0), At(1, 0), At(1, 1), At(0, 1) }.Select(p => new[] { p.X, p.Y }).ToArray();
        return new ScanOutline
        {
            Corners = corners,
            Top = Edge(t => At(t, 0)),
            Right = Edge(t => At(1, t)),
            Bottom = Edge(t => At(t, 1)),
            Left = Edge(t => At(0, t)),
            Confident = true
        };
    }

    static Mat MakePageTexture(int width, int height, Random rng)
    {
        var texture = new Mat(height, width, MatType.CV_8UC3, new Scalar(236, 240, 244)); // warm white (BGR)

        // Text-like lines: rows of dark word blocks.
        for (var y = 90; y < height - 90; y += 44)
        {
            var x = 60;
            while (x < width - 120)
            {
                var wordWidth = rng.Next(24, 110);
                if (x + wordWidth > width - 60) break;
                Cv2.Rectangle(texture, new Rect(x, y, wordWidth, 15), new Scalar(48, 46, 44), -1);
                x += wordWidth + rng.Next(14, 26);
            }
        }

        // Straight reference bars at known heights, used to prove the page comes out straight.
        foreach (var fraction in BarFractions)
        {
            var y = (int)(fraction * height);
            Cv2.Rectangle(texture, new Rect(40, y - 3, width - 80, 7), new Scalar(40, 120, 40), -1); // green: distinguishable from the gray "text"
        }

        // Red markers at the four corners: if they all survive, nothing at the edges was cut off.
        const int size = 30, inset = 8;
        var red = new Scalar(30, 30, 210);
        Cv2.Rectangle(texture, new Rect(inset, inset, size, size), red, -1);
        Cv2.Rectangle(texture, new Rect(width - inset - size, inset, size, size), red, -1);
        Cv2.Rectangle(texture, new Rect(width - inset - size, height - inset - size, size, size), red, -1);
        Cv2.Rectangle(texture, new Rect(inset, height - inset - size, size, size), red, -1);
        return texture;
    }

    static Mat MakeBackdrop(int width, int height, Backdrop kind, Random rng)
    {
        Scalar baseColor = kind switch
        {
            Backdrop.DarkWood => new Scalar(38, 58, 84),
            Backdrop.BlueCloth => new Scalar(150, 96, 52),
            Backdrop.TanWood => new Scalar(110, 150, 190),
            Backdrop.Granite => new Scalar(120, 122, 124),
            Backdrop.LightDesk => new Scalar(196, 202, 206),
            _ => new Scalar(222, 226, 228)
        };
        var backdrop = new Mat(height, width, MatType.CV_8UC3, baseColor);

        using var coarse = new Mat(Math.Max(2, height / 40), Math.Max(2, width / 40), MatType.CV_32FC3);
        Cv2.Randn(coarse, Scalar.All(0), Scalar.All(kind == Backdrop.WhiteDesk ? 4 : kind == Backdrop.LightDesk ? 7 : 16));
        using var noise = new Mat();
        Cv2.Resize(coarse, noise, new Size(width, height), 0, 0, InterpolationFlags.Cubic);
        using var floatBackdrop = new Mat();
        backdrop.ConvertTo(floatBackdrop, MatType.CV_32FC3);
        Cv2.Add(floatBackdrop, noise, floatBackdrop);

        if (kind is Backdrop.DarkWood or Backdrop.TanWood)
        {
            // Wood grain: faint stripes with a lazy wobble.
            var phase = rng.NextDouble() * 6;
            var grain = new Mat(height, width, MatType.CV_32FC1);
            var rows = new float[width];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                    rows[x] = (float)(7 * Math.Sin(y / 5.0 + 3 * Math.Sin(x / 140.0 + phase)));
                System.Runtime.InteropServices.Marshal.Copy(rows, 0, grain.Ptr(y), width);
            }
            using var grain3 = new Mat();
            Cv2.Merge(new[] { grain, grain, grain }, grain3);
            Cv2.Add(floatBackdrop, grain3, floatBackdrop);
            grain.Dispose();
        }

        floatBackdrop.ConvertTo(backdrop, MatType.CV_8UC3);
        return backdrop;
    }

    static void ApplyLighting(Mat photo, Random rng)
    {
        // A smooth brightness gradient across the frame (a window on one side) plus sensor noise.
        var angle = rng.NextDouble() * Math.PI * 2;
        double gx = Math.Cos(angle), gy = Math.Sin(angle);
        using var gain = new Mat(photo.Rows, photo.Cols, MatType.CV_32FC1);
        var row = new float[photo.Cols];
        for (var y = 0; y < photo.Rows; y++)
        {
            for (var x = 0; x < photo.Cols; x++)
            {
                var t = ((x / (double)photo.Cols - 0.5) * gx + (y / (double)photo.Rows - 0.5) * gy);
                row[x] = (float)(0.92 + 0.22 * t);
            }
            System.Runtime.InteropServices.Marshal.Copy(row, 0, gain.Ptr(y), photo.Cols);
        }
        using var gain3 = new Mat();
        Cv2.Merge(new[] { gain, gain, gain }, gain3);
        using var f = new Mat();
        photo.ConvertTo(f, MatType.CV_32FC3);
        Cv2.Multiply(f, gain3, f);
        using var noise = new Mat(photo.Rows, photo.Cols, MatType.CV_32FC3);
        Cv2.Randn(noise, Scalar.All(0), Scalar.All(3));
        Cv2.Add(f, noise, f);
        f.ConvertTo(photo, MatType.CV_8UC3);
    }

    public void Dispose()
    {
        Photo.Dispose();
        TruthMask.Dispose();
    }
}
