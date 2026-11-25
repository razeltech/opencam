using OpenCam.Core.Models;
using OpenCam.Storage;

namespace OpenCam.Tests;

public sealed class StorageTests
{
    [Fact]
    public async Task SqliteStorage_Persists_Settings_Devices_And_Audit_Events()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), "OpenCamStorageTests", Guid.NewGuid().ToString("N"), "test.db");
        try
        {
            using var storage = new SqliteStorageService(tempDb);

            var settings = new AppSettings
            {
                PreferredPort = 9443,
                RequirePin = true,
                PairingPin = "4821",
                DefaultQuality = VideoQualityPreset.High1080p,
                Theme = "Light"
            };
            await storage.SaveSettingsAsync(settings);

            var loaded = await storage.LoadSettingsAsync();
            Assert.Equal(9443, loaded.PreferredPort);
            Assert.True(loaded.RequirePin);
            Assert.Equal("4821", loaded.PairingPin);
            Assert.Equal(VideoQualityPreset.High1080p, loaded.DefaultQuality);
            Assert.Equal("Light", loaded.Theme);

            var now = DateTimeOffset.UtcNow;
            await storage.UpsertDeviceAsync(new PairedDeviceRecord(
                "dev-1", "Pixel 9", "Chrome/Android", "192.168.1.42", now, now, false));
            await storage.SetDeviceRevokedAsync("dev-1", true);

            var devices = await storage.GetDevicesAsync();
            Assert.Single(devices);
            Assert.True(devices[0].IsRevoked);

            await storage.AppendAuditEventAsync("DEVICE_REVOKED", "dev-1", "Revoked by user");
            var events = await storage.GetRecentAuditEventsAsync();
            Assert.Single(events);
            Assert.Equal("DEVICE_REVOKED", events[0].EventType);
        }
        finally
        {
            var dir = Path.GetDirectoryName(tempDb);
            if (dir is not null && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
