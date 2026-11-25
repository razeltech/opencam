using System.Net.Sockets;
using OpenCam.Core.Logging;
using OpenCam.Core.Networking;

namespace OpenCam.Tests;

public sealed class CoreTests
{
    [Fact]
    public void Logger_Redacts_Secrets_And_Bounds_RingBuffer()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "OpenCamTests", Guid.NewGuid().ToString("N"));
        try
        {
            using var logger = new AppLogger(tempDir, maxMemoryEntries: 50);

            logger.Info("Auth", "Pairing URL https://192.168.1.5:8443/?token=supersecret123&pin=9988");
            logger.Warn("HTTP", "Authorization: Bearer abc.def.ghi");
            logger.Debug("WS", "{\"token\":\"my-jwt-token\"}");

            var entries = logger.GetEntries();
            Assert.Equal(3, entries.Count);
            Assert.DoesNotContain("supersecret123", entries[0].Message);
            Assert.DoesNotContain("9988", entries[0].Message);
            Assert.Contains("[REDACTED]", entries[0].Message);
            Assert.DoesNotContain("abc.def.ghi", entries[1].Message);
            Assert.DoesNotContain("my-jwt-token", entries[2].Message);

            for (var i = 0; i < 100; i++)
            {
                logger.Info("Load", $"Entry {i}");
            }

            Assert.True(logger.GetEntries().Count <= 50);
            Assert.True(Directory.GetFiles(tempDir, "opencam-*.log").Length >= 1);
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
    public void LanNetworkService_Finds_Available_Port_When_Preferred_Is_Busy()
    {
        using var occupied = new TcpListener(System.Net.IPAddress.Any, 0);
        occupied.Start();
        var busyPort = ((System.Net.IPEndPoint)occupied.LocalEndpoint).Port;

        var chosen = LanNetworkService.FindAvailableTcpPort(busyPort);
        Assert.NotEqual(busyPort, chosen);
        Assert.InRange(chosen, 1024, 65535);
    }

    [Fact]
    public void LanNetworkService_Enumerates_Adapters_Without_Loopback()
    {
        var adapters = LanNetworkService.GetActiveIPv4Adapters();
        Assert.All(adapters, a =>
        {
            Assert.False(string.IsNullOrWhiteSpace(a.IPv4Address));
            Assert.False(a.IPv4Address.StartsWith("127.", StringComparison.Ordinal));
            Assert.False(a.IPv4Address.StartsWith("169.254.", StringComparison.Ordinal));
        });
    }
}
