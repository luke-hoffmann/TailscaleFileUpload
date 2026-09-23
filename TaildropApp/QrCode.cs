using System.Drawing;
using QRCoder;

namespace TaildropApp;

static class QrCode
{
    static readonly Color Dark = Color.FromArgb(0x15, 0x17, 0x14);
    static readonly Color Light = Color.White;

    public static byte[] GeneratePng(string address)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(address, QRCodeGenerator.ECCLevel.M);
        var pngQr = new PngByteQRCode(data);
        return pngQr.GetGraphic(10, Dark, Light, drawQuietZones: true);
    }

    public static string GenerateDataUrl(string address)
    {
        var bytes = GeneratePng(address);
        return $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
    }
}
