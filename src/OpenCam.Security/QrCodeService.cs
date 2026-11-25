using QRCoder;

namespace OpenCam.Security;

public static class QrCodeService
{
    public static byte[] GeneratePngBytes(string url, int pixelsPerModule = 10)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        var pngQr = new PngByteQRCode(data);
        return pngQr.GetGraphic(pixelsPerModule);
    }
}
