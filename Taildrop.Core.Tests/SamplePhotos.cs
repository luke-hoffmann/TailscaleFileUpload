using OpenCvSharp;
using Xunit;

namespace Taildrop.Core.Tests;

/// <summary>
/// Writes realistic-looking sample photos for the browser tests in tools/e2e. Only runs when the environment
/// variable TAILDROP_SAMPLES_DIR is set, e.g.  TAILDROP_SAMPLES_DIR=/tmp/samples dotnet test --filter SamplePhotos
/// </summary>
public class SamplePhotos
{
    [Fact]
    public void WriteSamples()
    {
        var directory = Environment.GetEnvironmentVariable("TAILDROP_SAMPLES_DIR");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);

        var samples = new (string Name, SceneOptions Options)[]
        {
            ("a4-tilted", new SceneOptions { Width = 4032, Height = 3024, TiltDeg = 18, YawDeg = 9, RollDeg = 4, PillowXPx = 30, Backdrop = Backdrop.DarkWood }),
            ("receipt-curled", new SceneOptions { Width = 3024, Height = 4032, PageMmWidth = 80, PageMmHeight = 240, TiltDeg = 22, YawDeg = 8, RollDeg = -10, Fill = 0.8, PillowXPx = 40, Backdrop = Backdrop.BlueCloth }),
            ("no-page", new SceneOptions { Width = 3024, Height = 4032, Backdrop = Backdrop.Granite, Fill = 0.02 }),
            ("white-desk", new SceneOptions { Width = 3024, Height = 4032, Backdrop = Backdrop.WhiteDesk, Fill = 0.6 }),
        };
        foreach (var (name, options) in samples)
        {
            using var scene = new SyntheticScene(options);
            Cv2.ImWrite(Path.Combine(directory, name + ".jpg"), scene.Photo, new ImageEncodingParam(ImwriteFlags.JpegQuality, 88));
        }
        File.WriteAllBytes(Path.Combine(directory, "not-a-photo.jpg"), Enumerable.Range(0, 5000).Select(i => (byte)(i * 31)).ToArray());
        WriteLiveCamera(Path.Combine(directory, "live-camera.mjpeg"));
        File.WriteAllText(Path.Combine(directory, "notes.txt"), "hello from the phone\n");
        File.WriteAllBytes(Path.Combine(directory, "movie.mp4"), new byte[300_000]);
    }

    /// <summary>
    /// A phone's view while scanning, for Chromium's fake camera (--use-file-for-fake-video-capture, 30 frames a
    /// second, looping): a page held still, close-ups sweeping over it, the same page again, then the next page.
    /// </summary>
    static void WriteLiveCamera(string path)
    {
        var size = new Size(1080, 1920);
        using var first = SyntheticDocument.Make(2480, 3508, seed: 31);
        using var second = SyntheticDocument.Make(2480, 3508, seed: 32);
        using var output = File.Create(path);

        void Hold(Mat frame, int frames)
        {
            Cv2.ImEncode(".jpg", frame, out var jpeg, new ImageEncodingParam(ImwriteFlags.JpegQuality, 80));
            for (var i = 0; i < frames; i++) output.Write(jpeg);
        }

        var (whole, _) = SyntheticDocument.WholePage(first, size);
        using (whole) Hold(whole, 90);                                      // 3 s: the page, held still
        foreach (var closeUp in SyntheticDocument.CloseUps(first, size, columns: 4, rows: 5, density: 1.2, seed: 6))
            using (closeUp) Hold(closeUp, 30);                              // 20 s: a slow sweep, 1 s per spot
        var (again, _) = SyntheticDocument.WholePage(first, size);
        using (again) Hold(again, 60);                                      // 2 s: the same page again
        var (next, _) = SyntheticDocument.WholePage(second, size);
        using (next) Hold(next, 120);                                       // 4 s: the next page
    }
}
