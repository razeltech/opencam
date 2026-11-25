using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using OpenCam.Core.Logging;

namespace OpenCam.Security;

public sealed class LanCertificateService
{
    private readonly string _certDirectory;
    private readonly AppLogger _logger;

    public string PfxPath => Path.Combine(_certDirectory, "opencam-lan.pfx");
    public string PublicCertPath => Path.Combine(_certDirectory, "opencam-lan.cer");

    public LanCertificateService(AppLogger logger, string? certDirectory = null)
    {
        _logger = logger;
        _certDirectory = certDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenCam",
            "Certs");
        Directory.CreateDirectory(_certDirectory);
    }

    public X509Certificate2 GetOrCreateLanCertificate(string lanIpAddress)
    {
        if (File.Exists(PfxPath))
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(
                    PfxPath,
                    password: null,
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);

                if (existing.NotAfter > DateTimeOffset.UtcNow.AddDays(7) &&
                    CertificateContainsIp(existing, lanIpAddress))
                {
                    _logger.Info("Security", $"Loaded existing LAN HTTPS certificate (thumbprint {existing.Thumbprint[..8]}...)");
                    return existing;
                }

                existing.Dispose();
                _logger.Info("Security", "Existing LAN certificate expired or IP changed; regenerating.");
            }
            catch (Exception ex)
            {
                _logger.Warn("Security", $"Failed to load existing LAN certificate, regenerating: {ex.Message}");
            }
        }

        return GenerateAndSaveCertificate(lanIpAddress);
    }

    private X509Certificate2 GenerateAndSaveCertificate(string lanIpAddress)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var subject = new X500DistinguishedName("CN=OpenCam Local Server, O=OpenCam");
        var req = new CertificateRequest(subject, ecdsa, HashAlgorithmName.SHA256);

        req.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName(Environment.MachineName);
        san.AddIpAddress(IPAddress.Loopback);
        if (IPAddress.TryParse(lanIpAddress, out var parsedIp))
        {
            san.AddIpAddress(parsedIp);
        }
        req.CertificateExtensions.Add(san.Build());

        var now = DateTimeOffset.UtcNow;
        using var created = req.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(365));

        var pfxBytes = created.Export(X509ContentType.Pfx);
        File.WriteAllBytes(PfxPath, pfxBytes);

        var cerBytes = created.Export(X509ContentType.Cert);
        File.WriteAllBytes(PublicCertPath, cerBytes);

        var reloadable = X509CertificateLoader.LoadPkcs12(
            pfxBytes,
            password: null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);

        _logger.Info("Security", $"Generated self-signed LAN HTTPS cert for {lanIpAddress} (valid 365d, thumbprint {reloadable.Thumbprint[..8]}...)");
        return reloadable;
    }

    private static bool CertificateContainsIp(X509Certificate2 cert, string ipAddress)
    {
        if (!IPAddress.TryParse(ipAddress, out var targetIp)) return true;

        foreach (var ext in cert.Extensions)
        {
            if (ext.Oid?.Value != "2.5.29.17") continue;
            var formatted = ext.Format(false);
            if (formatted.Contains(targetIp.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
