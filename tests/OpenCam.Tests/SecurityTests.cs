using OpenCam.Core.Logging;
using OpenCam.Security;

namespace OpenCam.Tests;

public sealed class SecurityTests
{
    [Fact]
    public void PairingAuthService_Validates_Token_Pin_And_Revocation()
    {
        var auth = new PairingAuthService();
        var token = auth.RotateMasterPairingToken(60);

        Assert.False(auth.ValidatePairingRequest("wrong", null, false, null));
        Assert.True(auth.ValidatePairingRequest(token, null, false, null));

        Assert.False(auth.ValidatePairingRequest(token, "0000", true, "1234"));
        Assert.True(auth.ValidatePairingRequest(token, "1234", true, "1234"));

        var sessionToken = auth.IssueDeviceSessionToken(60);
        Assert.True(auth.ValidateDeviceSession(sessionToken, "dev-abc"));

        auth.RevokeDevice("dev-abc");
        Assert.False(auth.ValidateDeviceSession(sessionToken, "dev-abc"));

        auth.UnrevokeDevice("dev-abc");
        Assert.True(auth.ValidateDeviceSession(sessionToken, "dev-abc"));
    }

    [Fact]
    public void LanCertificateService_Generates_Valid_SelfSigned_Cert_With_San()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "OpenCamCertTests", Guid.NewGuid().ToString("N"));
        try
        {
            using var logger = new AppLogger(tempDir);
            var certService = new LanCertificateService(logger, tempDir);
            using var cert = certService.GetOrCreateLanCertificate("192.168.1.100");

            Assert.True(cert.HasPrivateKey);
            Assert.True(File.Exists(certService.PfxPath));
            Assert.True(File.Exists(certService.PublicCertPath));

            using var reloaded = certService.GetOrCreateLanCertificate("192.168.1.100");
            Assert.Equal(cert.Thumbprint, reloaded.Thumbprint);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void QrCodeService_Produces_Valid_Png()
    {
        var png = QrCodeService.GeneratePngBytes("https://192.168.1.100:8443/?token=test123");
        Assert.True(png.Length > 100);
        Assert.Equal(0x89, png[0]);
        Assert.Equal(0x50, png[1]);
        Assert.Equal(0x4E, png[2]);
        Assert.Equal(0x47, png[3]);
    }
}
