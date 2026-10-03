using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using OpenCvSharp;
using Taildrop.Core.Scanning;

// ScanBench: run the shipping scanner over benchmark photos.
//   synthetic <samples dir> <results dir>   every sample folder with photo.jpg -> scan.jpg, outline.json, overlay.jpg
//   smartdoc  <frames root> <results.csv>   detection only, one CSV row per frame (corners as normalized TL,TR,BR,BL)
//   photos    <folder of jpgs> <results dir> real photos without ground truth (visual review)
if (args.Length < 3)
{
    Console.Error.WriteLine("usage: ScanBench synthetic|smartdoc|photos <input> <output>");
    return 2;
}

var mode = args[0];
var input = args[1];
var output = args[2];
if (Environment.GetEnvironmentVariable("SCANBENCH_RANKER") == "off") DocumentDetector.UseRanker = false;
if (Environment.GetEnvironmentVariable("SCANBENCH_CLEAN") == "off") EdgeCleaner.Enabled = false;
var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };

if (mode is "synthetic" or "photos")
{
    Directory.CreateDirectory(output);
    var photos = mode == "synthetic"
        ? Directory.GetDirectories(input).Where(d => File.Exists(Path.Combine(d, "photo.jpg"))).Select(d => (Name: Path.GetFileName(d), Path: Path.Combine(d, "photo.jpg"))).ToList()
        : Directory.GetFiles(input).Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            .Select(f => (Name: Path.GetFileNameWithoutExtension(f), Path: f)).ToList();
    var done = 0;
    Parallel.ForEach(photos.OrderBy(p => p.Name), options, photo =>
    {
        var dir = Path.Combine(output, photo.Name);
        Directory.CreateDirectory(dir);
        var clock = Stopwatch.StartNew();
        using var image = ScanPipeline.Decode(File.ReadAllBytes(photo.Path));
        var decodeMs = clock.ElapsedMilliseconds;
        clock.Restart();
        var outline = ScanPipeline.Detect(image);
        var detectMs = clock.ElapsedMilliseconds;
        clock.Restart();
        var render = ScanPipeline.Render(image, outline, ScanFilter.Original, 0);
        var renderMs = clock.ElapsedMilliseconds;
        File.WriteAllBytes(Path.Combine(dir, "scan.jpg"), render.Jpeg);
        File.WriteAllText(Path.Combine(dir, "outline.json"), JsonSerializer.Serialize(new
        {
            outline.Corners, outline.Top, outline.Right, outline.Bottom, outline.Left, outline.Confident,
            image = new[] { image.Width, image.Height },
            ms = new { decode = decodeMs, detect = detectMs, render = renderMs }
        }));
        WriteOverlay(image, outline, Path.Combine(dir, "overlay.jpg"));
        var n = Interlocked.Increment(ref done);
        if (n % 25 == 0) Console.WriteLine($"{n}/{photos.Count}");
    });
    Console.WriteLine($"done {done}");
    return 0;
}

if (mode == "smartdoc")
{
    // input = frames root containing backgroundNN/<model>/frame_XXXX.jpeg
    var frames = Directory.GetFiles(input, "*.jpeg", SearchOption.AllDirectories).OrderBy(f => f).ToList();
    var rows = new ConcurrentBag<string>();
    Parallel.ForEach(frames, options, frame =>
    {
        using var image = Cv2.ImRead(frame, ImreadModes.Color);
        var clock = Stopwatch.StartNew();
        var outline = ScanPipeline.Detect(image);
        var ms = clock.ElapsedMilliseconds;
        var rel = Path.GetRelativePath(input, frame).Replace('\\', '/');
        var c = outline.Corners;
        rows.Add(FormattableString.Invariant($"{rel},{outline.Confident},{ms},{c[0][0]},{c[0][1]},{c[1][0]},{c[1][1]},{c[2][0]},{c[2][1]},{c[3][0]},{c[3][1]},{image.Width},{image.Height}"));
    });
    File.WriteAllLines(output, new[] { "image_path,confident,ms,x0,y0,x1,y1,x2,y2,x3,y3,width,height" }.Concat(rows.OrderBy(r => r)));
    Console.WriteLine($"done {rows.Count}");
    return 0;
}

if (mode == "dump")
{
    // Candidate features for training the page ranker. input = synthetic samples dir or SmartDoc frames root.
    var files = Directory.GetDirectories(input).Any(d => File.Exists(Path.Combine(d, "photo.jpg")))
        ? Directory.GetDirectories(input).Select(d => Path.Combine(d, "photo.jpg")).Where(File.Exists).ToList()
        : Directory.GetFiles(input, "*.jpeg", SearchOption.AllDirectories).ToList();
    var lines = new ConcurrentBag<string>();
    Parallel.ForEach(files, options, file =>
    {
        using var image = Cv2.ImRead(file, ImreadModes.Color);
        var scale = Math.Min(1.0, DocumentDetector.WorkEdge / (double)Math.Max(image.Width, image.Height));
        using var small = new Mat();
        Cv2.Resize(image, small, new Size(), scale, scale, InterpolationFlags.Area);
        using var evidence = new QuadFinder.Evidence(small);
        var scored = DocumentDetector.Candidates(small, evidence);
        if (scored.Count == 0) return;
        var features = PageRanker.Features(evidence, scored);
        var chosen = QuadFinder.WholeSheet(evidence, scored);
        lines.Add(JsonSerializer.Serialize(new
        {
            path = Path.GetRelativePath(input, file).Replace('\\', '/'),
            w = small.Width, h = small.Height,
            chosen = scored.FindIndex(q => q.Corners.SequenceEqual(chosen.Corners)),
            cands = scored.Select((q, i) => new { c = q.Corners.Select(p => new[] { p.X / small.Width, p.Y / small.Height }), f = features[i] }),
        }));
    });
    File.WriteAllLines(output, lines.OrderBy(l => l));
    Console.WriteLine($"done {lines.Count}");
    return 0;
}

if (mode == "profile")
{
    // input = a photo; prints the time of each detection stage (second run, after JIT warm-up)
    using var image = ScanPipeline.Decode(File.ReadAllBytes(input));
    for (var run = 0; run < 2; run++)
    {
        var clock = Stopwatch.StartNew();
        var scale = Math.Min(1.0, DocumentDetector.WorkEdge / (double)Math.Max(image.Width, image.Height));
        using var small = new Mat();
        Cv2.Resize(image, small, new Size(), scale, scale, InterpolationFlags.Area);
        using var evidence = new QuadFinder.Evidence(small);
        var t0 = clock.ElapsedMilliseconds;
        var scored = DocumentDetector.Candidates(small, evidence);
        var t1 = clock.ElapsedMilliseconds;
        var features = PageRanker.Features(evidence, scored);
        var t2 = clock.ElapsedMilliseconds;
        foreach (var f in features) PageRanker.Predict(f);
        var t3 = clock.ElapsedMilliseconds;
        var outline = ScanPipeline.Detect(image);
        var t4 = clock.ElapsedMilliseconds;
        if (run == 1) Console.WriteLine($"resize+evidence {t0} candidates {t1 - t0} ({scored.Count}) features {t2 - t1} predict {t3 - t2} | full detect {t4 - t3}");
    }
    return 0;
}

if (mode == "lines")
{
    using var image = Cv2.ImRead(input, ImreadModes.Color);
    var scale = Math.Min(1.0, DocumentDetector.WorkEdge / (double)Math.Max(image.Width, image.Height));
    using var small = new Mat();
    Cv2.Resize(image, small, new Size(), scale, scale, InterpolationFlags.Area);
    using var evidence = new QuadFinder.Evidence(small);
    QuadFinder.LineHypotheses(small, evidence);
    var i = 0;
    foreach (var (a, b) in QuadFinder.LastLines!)
    {
        Cv2.Line(small, new Point((int)a.X, (int)a.Y), new Point((int)b.X, (int)b.Y), i < 8 ? Scalar.Red : Scalar.Blue, 2);
        Cv2.PutText(small, (i++).ToString(), new Point((int)((a.X + b.X) / 2), (int)((a.Y + b.Y) / 2)), HersheyFonts.HersheySimplex, 0.5, Scalar.Yellow, 1);
    }
    Cv2.ImWrite(output, small);
    return 0;
}

if (mode == "debug")
{
    // input = a synthetic sample folder; prints the true outline's score and the top hypotheses, draws them into <output>.jpg
    using var image = ScanPipeline.Decode(File.ReadAllBytes(Path.Combine(input, "photo.jpg")));
    var scale = Math.Min(1.0, DocumentDetector.WorkEdge / (double)Math.Max(image.Width, image.Height));
    using var small = new Mat();
    Cv2.Resize(image, small, new Size(), scale, scale, InterpolationFlags.Area);
    using var evidence = new QuadFinder.Evidence(small);
    string Fmt(QuadFinder.Breakdown b) => FormattableString.Invariant($"total {b.Total:F3} sup {b.Support:F2} str {b.Strength:F2} con {b.Contrast:F2} paper {b.Paper:F2} shape {b.Shape:F2} size {b.Size:F2} border {b.Border:F2}");
    using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(input, "meta.json")));
    foreach (var key in new[] { "corners_actual", "corners_virtual" })
    {
        var truth = meta.RootElement.GetProperty(key).EnumerateArray().Select(p => new Point2d(p[0].GetDouble() * scale, p[1].GetDouble() * scale)).ToArray();
        Console.WriteLine($"TRUTH {key}: {Fmt(QuadFinder.Score(evidence, truth))}");
    }
    var scored = DocumentDetector.Candidates(small, evidence);
    var truthQ = meta.RootElement.GetProperty("corners_actual").EnumerateArray().Select(p => new Point2d(p[0].GetDouble() * scale, p[1].GetDouble() * scale)).ToArray();
    double Match(Point2d[] q) => Enumerable.Range(0, 4).Min(r => Enumerable.Range(0, 4).Average(i => Math.Sqrt(Math.Pow(q[(i + r) % 4].X - truthQ[i].X, 2) + Math.Pow(q[(i + r) % 4].Y - truthQ[i].Y, 2))));
    var nearest = scored.Select((q, i) => (q, i)).OrderBy(x => Match(x.q.Corners)).First();
    var top = scored[0];
    Console.WriteLine(FormattableString.Invariant($"NEAREST-TO-TRUTH rank {nearest.i} {nearest.q.Source} score {nearest.q.Score:F3} err {Match(nearest.q.Corners):F1}px  areaRatio {Math.Abs(Geometry.SignedArea(nearest.q.Corners)) / Math.Abs(Geometry.SignedArea(top.Corners)):F2} overlap {QuadFinder.Overlap(top.Corners, nearest.q.Corners) / Math.Abs(Geometry.SignedArea(top.Corners)):F2} cornersOn {QuadFinder.CornersOnOutline(top.Corners, nearest.q.Corners)} print {QuadFinder.PrintInsidePage(evidence, top.Corners, nearest.q.Corners)} extraPaper {QuadFinder.ExtraIsPaper(evidence, top.Corners, nearest.q.Corners)}"));
    var chosen = QuadFinder.WholeSheet(evidence, scored);
    Console.WriteLine($"CHOSEN {chosen.Source} {chosen.Score:F3}");
    using var canvas = small.Clone();
    var colors = new[] { Scalar.Red, Scalar.Orange, Scalar.Yellow, Scalar.Green, Scalar.Cyan, Scalar.Blue, Scalar.Magenta, Scalar.White };
    for (var i = 0; i < Math.Min(8, scored.Count); i++)
    {
        var q = scored[i];
        Console.WriteLine($"#{i} {q.Source,-11} {Fmt(QuadFinder.Score(evidence, q.Corners))}");
        Cv2.Polylines(canvas, new[] { q.Corners.Select(p => new Point((int)p.X, (int)p.Y)).ToArray() }, true, colors[i], 2);
    }
    Cv2.ImWrite(output + ".jpg", canvas);
    return 0;
}

Console.Error.WriteLine("unknown mode " + mode);
return 2;

static void WriteOverlay(Mat image, ScanOutline outline, string path)
{
    var scale = 900.0 / Math.Max(image.Width, image.Height);
    using var small = image.Resize(new Size(), scale, scale, InterpolationFlags.Area);
    Point P(double[] p) => new((int)Math.Round(p[0] * small.Width), (int)Math.Round(p[1] * small.Height));
    foreach (var edge in new[] { outline.Top, outline.Right, outline.Bottom, outline.Left })
        for (var i = 1; i < edge.Length; i++) Cv2.Line(small, P(edge[i - 1]), P(edge[i]), new Scalar(0, 255, 255), 2, LineTypes.AntiAlias);
    foreach (var corner in outline.Corners) Cv2.Circle(small, P(corner), 5, new Scalar(255, 0, 255), -1, LineTypes.AntiAlias);
    Cv2.ImWrite(path, small, new ImageEncodingParam(ImwriteFlags.JpegQuality, 80));
}
