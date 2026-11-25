using System.Net.Http;
using OpenCam.Core.Logging;
using OpenCam.Core.Models;
using OpenCam.Core.Networking;
using OpenCam.Security;
using OpenCam.Server;
using OpenCam.Storage;
using OpenCam.WebRTC;

namespace OpenCam.Tests;

public sealed class ServerTests
{
    [Fact]
    public async Task LocalWebServer_Starts_Serves_Mobile_Ui_And_Health_Over_Https()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "OpenCamServerTests", Guid.NewGuid().ToString("N"));
        try
        {
            using var logger = new AppLogger(tempDir);
            using var storage = new SqliteStorageService(Path.Combine(tempDir, "test.db"));
            var auth = new PairingAuthService();
            var certs = new LanCertificateService(logger, Path.Combine(tempDir, "certs"));
            using var webrtc = new WebRtcSessionManager(logger);
            await using var server = new LocalWebServer(logger, auth, certs, storage, webrtc);

            var port = LanNetworkService.FindAvailableTcpPort(18443);
            await server.StartAsync("127.0.0.1", port, new AppSettings());

            Assert.True(server.IsRunning);
            Assert.NotNull(server.PairingUrl);

            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            using var client = new HttpClient(handler);

            var health = await client.GetAsync($"https://127.0.0.1:{port}/health");
            Assert.True(health.IsSuccessStatusCode);

            var index = await client.GetStringAsync($"https://127.0.0.1:{port}/");
            Assert.Contains("OpenCam Mobile", index);

            var jsMatch = System.Text.RegularExpressions.Regex.Match(index, @"src=""(/assets/[^""]+\.js)""");
            Assert.True(jsMatch.Success, "Expected JS asset reference in index.html");
            var jsResp = await client.GetAsync($"https://127.0.0.1:{port}{jsMatch.Groups[1].Value}");
            Assert.True(jsResp.IsSuccessStatusCode);
            Assert.Equal("application/javascript", jsResp.Content.Headers.ContentType?.MediaType);

            var cssMatch = System.Text.RegularExpressions.Regex.Match(index, @"href=""(/assets/[^""]+\.css)""");
            Assert.True(cssMatch.Success, "Expected CSS asset reference in index.html");
            var cssResp = await client.GetAsync($"https://127.0.0.1:{port}{cssMatch.Groups[1].Value}");
            Assert.True(cssResp.IsSuccessStatusCode);
            Assert.Equal("text/css", cssResp.Content.Headers.ContentType?.MediaType);

            await server.StopAsync();
            Assert.False(server.IsRunning);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
