using System.Text.Json.Serialization;

namespace OpenCam.Core.Models;

public enum DeviceConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Streaming,
    Reconnecting,
    Error
}

public enum VideoQualityPreset
{
    Low480p,
    Balanced720p,
    High1080p,
    Ultra1440p
}

public enum CameraFacing
{
    Unknown,
    User,
    Environment
}

public sealed record CameraCapabilities
{
    public bool SupportsTorch { get; init; }
    public bool SupportsZoom { get; init; }
    public double MinZoom { get; init; } = 1.0;
    public double MaxZoom { get; init; } = 1.0;
    public bool SupportsExposureCompensation { get; init; }
    public double MinExposure { get; init; }
    public double MaxExposure { get; init; }
    public bool SupportsFocusMode { get; init; }
    public bool SupportsTapToFocus { get; init; }
    public List<string> SupportedResolutions { get; init; } = [];
    public List<int> SupportedFrameRates { get; init; } = [];
}

public sealed record StreamTelemetry
{
    public string DeviceId { get; init; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }
    public double Fps { get; init; }
    public double BitrateKbps { get; init; }
    public double LatencyMs { get; init; }
    public double PacketLossPercent { get; init; }
    public string Codec { get; init; } = "VP8";
    public string QualityLabel { get; init; } = "Good";
    public double AudioLevelDb { get; init; } = -60.0;
    public bool AudioEnabled { get; init; }
    public int? BatteryPercent { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class ConnectedDevice
{
    public string DeviceId { get; init; } = string.Empty;
    public string Name { get; set; } = "Mobile Device";
    public string UserAgent { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public DeviceConnectionState State { get; set; } = DeviceConnectionState.Connected;
    public bool IsActiveVirtualCamSource { get; set; }
    public DateTimeOffset ConnectedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public CameraCapabilities Capabilities { get; set; } = new();
    public StreamTelemetry Telemetry { get; set; } = new();
}

public sealed class AppSettings
{
    public int PreferredPort { get; set; } = 8443;
    public string? PreferredAdapterId { get; set; }
    public bool AutoStartServer { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool AutoReconnectDevices { get; set; } = true;
    public bool RequirePin { get; set; }
    public string? PairingPin { get; set; }
    public int SessionExpirationMinutes { get; set; } = 480;
    public VideoQualityPreset DefaultQuality { get; set; } = VideoQualityPreset.Balanced720p;
    public int TargetFps { get; set; } = 30;
    public bool EnableHardwareAcceleration { get; set; } = true;
    public string VirtualCameraName { get; set; } = "OpenCam";
    public bool BlankWhenDisconnected { get; set; }
    public string Theme { get; set; } = "Dark";
}
