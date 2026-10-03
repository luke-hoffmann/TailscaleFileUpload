using OpenCvSharp;

namespace Taildrop.Core.Scanning;

/// <summary>Raised for problems the phone user can understand and fix (unreadable photo, too large, ...).</summary>
public sealed class ScanException : Exception
{
    public int StatusCode { get; }
    public ScanException(int statusCode, string message) : base(message) => StatusCode = statusCode;
}

public sealed record ScanRender(byte[] Jpeg, byte[] Preview, int Width, int Height);

/// <summary>Decode → find page → flatten → clean up → encode. Stateless; the server owns sessions.</summary>
public static class ScanPipeline
{
    const long MaxPixels = 120_000_000;
    const int JpegQuality = 92;
    // OpenCV's IMWRITE_JPEG_SAMPLING_FACTOR (7) and IMWRITE_JPEG_SAMPLING_FACTOR_444 (0x111111); OpenCvSharp has no names for them.
    const ImwriteFlags JpegSamplingFactorFlag = (ImwriteFlags)7;
    const int JpegSampling444 = 0x111111;

    // Page rendering is memory-hungry (full-resolution maps and copies); keep a couple at a time.
    static readonly SemaphoreSlim Gate = new(2, 2);

    /// <summary>Decodes a photo (EXIF orientation applied). Throws <see cref="ScanException"/> for anything unusable.</summary>
    public static Mat Decode(byte[] data)
    {
        if (data.Length < 16) throw new ScanException(400, "That photo is empty");
        if (IsHeic(data)) throw new ScanException(415, "HEIC photos aren't supported here. Take the photo with the Scan button, or share it as a JPEG.");
        if (ImageProbe.TryGetSize(data, out var width, out var height) && (width * (long)height > MaxPixels || width > 30000 || height > 30000))
            throw new ScanException(413, "That photo is too large to scan");

        Mat? image = null;
        try
        {
            image = Cv2.ImDecode(data, ImreadModes.Color);
            if (image.Empty()) throw new ScanException(415, "That doesn't look like a photo I can read");
            return image;
        }
        catch (OpenCVException)
        {
            image?.Dispose();
            throw new ScanException(415, "That doesn't look like a photo I can read");
        }
    }

    public static ScanOutline Detect(Mat image) => DocumentDetector.Detect(image);

    /// <summary>Renders the page from a photo and outline. <paramref name="rotateClockwise"/> is 0, 90, 180 or 270.</summary>
    public static async Task<ScanRender> RenderAsync(Mat image, ScanOutline outline, ScanFilter filter, int rotateClockwise, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => Render(image, outline, filter, rotateClockwise), cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }

    public static ScanRender Render(Mat image, ScanOutline outline, ScanFilter filter, int rotateClockwise)
    {
        using var flat = PageFlattener.Flatten(image, outline);
        EdgeCleaner.Clean(image, outline, flat);
        using var enhanced = ScanEnhancer.Apply(flat, filter);
        using var rotated = Rotate(enhanced, rotateClockwise);

        var jpeg = Encode(rotated, JpegQuality);
        using var preview = Resize(rotated, 900);
        return new ScanRender(jpeg, Encode(preview, 80), rotated.Width, rotated.Height);
    }

    /// <summary>A screen-sized JPEG of the photo for the "adjust edges" view (coordinates match the outline's).</summary>
    public static byte[] EncodeSource(Mat image, int longEdge = 1600)
    {
        using var resized = Resize(image, longEdge);
        return Encode(resized, 82);
    }

    static Mat Rotate(Mat source, int clockwiseDegrees)
    {
        RotateFlags? flag = (((clockwiseDegrees % 360) + 360) % 360) switch
        {
            90 => RotateFlags.Rotate90Clockwise,
            180 => RotateFlags.Rotate180,
            270 => RotateFlags.Rotate90Counterclockwise,
            _ => null
        };
        if (flag is null) return source.Clone();
        var result = new Mat();
        Cv2.Rotate(source, result, flag.Value);
        return result;
    }

    static Mat Resize(Mat source, int longEdge)
    {
        var scale = Math.Min(1.0, longEdge / (double)Math.Max(source.Width, source.Height));
        if (scale >= 1) return source.Clone();
        var result = new Mat();
        Cv2.Resize(source, result, new Size(), scale, scale, InterpolationFlags.Area);
        return result;
    }

    static byte[] Encode(Mat image, int quality)
    {
        // 4:4:4 chroma (no subsampling) keeps colored print and thin colored lines crisp.
        Cv2.ImEncode(".jpg", image, out var bytes,
            new ImageEncodingParam(ImwriteFlags.JpegQuality, quality),
            new ImageEncodingParam(JpegSamplingFactorFlag, JpegSampling444));
        return bytes;
    }

    static bool IsHeic(byte[] data)
    {
        if (data.Length < 12 || data[4] != 'f' || data[5] != 't' || data[6] != 'y' || data[7] != 'p') return false;
        var brand = System.Text.Encoding.ASCII.GetString(data, 8, 4);
        return brand is "heic" or "heix" or "heim" or "heis" or "mif1" or "msf1" or "hevc";
    }
}

/// <summary>Reads pixel dimensions from JPEG/PNG headers without decoding, so absurd images are refused cheaply.</summary>
static class ImageProbe
{
    public static bool TryGetSize(byte[] d, out int width, out int height)
    {
        width = height = 0;
        if (d.Length > 24 && d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G')
        {
            width = (d[16] << 24) | (d[17] << 16) | (d[18] << 8) | d[19];
            height = (d[20] << 24) | (d[21] << 16) | (d[22] << 8) | d[23];
            return width > 0 && height > 0;
        }
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) return false;

        var i = 2;
        while (i + 9 < d.Length)
        {
            if (d[i] != 0xFF) { i++; continue; }
            var marker = d[i + 1];
            if (marker == 0xFF) { i++; continue; }
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) { i += 2; continue; }
            var length = (d[i + 2] << 8) | d[i + 3];
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                height = (d[i + 5] << 8) | d[i + 6];
                width = (d[i + 7] << 8) | d[i + 8];
                return width > 0 && height > 0;
            }
            if (length < 2) return false;
            i += 2 + length;
        }
        return false;
    }
}
