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
        File.WriteAllText(Path.Combine(directory, "notes.txt"), "hello from the phone\n");
        File.WriteAllBytes(Path.Combine(directory, "movie.mp4"), new byte[300_000]);
    }
}
