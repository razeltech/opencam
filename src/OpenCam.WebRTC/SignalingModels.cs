using System.Text.Json.Serialization;

namespace OpenCam.WebRTC;

public sealed record SignalingMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; init; }

    [JsonPropertyName("deviceName")]
    public string? DeviceName { get; init; }

    [JsonPropertyName("sdp")]
    public string? Sdp { get; init; }

    [JsonPropertyName("candidate")]
    public IceCandidatePayload? Candidate { get; init; }

    [JsonPropertyName("telemetry")]
    public ClientTelemetryPayload? Telemetry { get; init; }

    [JsonPropertyName("capabilities")]
    public ClientCapabilitiesPayload? Capabilities { get; init; }

    [JsonPropertyName("command")]
    public CameraCommandPayload? Command { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

public sealed record IceCandidatePayload
{
    [JsonPropertyName("candidate")]
    public string Candidate { get; init; } = string.Empty;

    [JsonPropertyName("sdpMid")]
    public string? SdpMid { get; init; }

    [JsonPropertyName("sdpMLineIndex")]
    public ushort sdpMLineIndex { get; init; }
}

public sealed record ClientTelemetryPayload
{
    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("height")]
    public int Height { get; init; }

    [JsonPropertyName("fps")]
    public double Fps { get; init; }

    [JsonPropertyName("batteryPercent")]
    public int? BatteryPercent { get; init; }

    [JsonPropertyName("audioEnabled")]
    public bool AudioEnabled { get; init; }

    [JsonPropertyName("sentTimestampMs")]
    public long SentTimestampMs { get; init; }
}

public sealed record ClientCapabilitiesPayload
{
    [JsonPropertyName("supportsTorch")]
    public bool SupportsTorch { get; init; }

    [JsonPropertyName("supportsZoom")]
    public bool SupportsZoom { get; init; }

    [JsonPropertyName("minZoom")]
    public double MinZoom { get; init; } = 1.0;

    [JsonPropertyName("maxZoom")]
    public double MaxZoom { get; init; } = 1.0;

    [JsonPropertyName("supportsExposure")]
    public bool SupportsExposure { get; init; }

    [JsonPropertyName("minExposure")]
    public double MinExposure { get; init; }

    [JsonPropertyName("maxExposure")]
    public double MaxExposure { get; init; }

    [JsonPropertyName("supportsFocusMode")]
    public bool SupportsFocusMode { get; init; }

    [JsonPropertyName("resolutions")]
    public List<string> Resolutions { get; init; } = [];

    [JsonPropertyName("frameRates")]
    public List<int> FrameRates { get; init; } = [];
}

public sealed record CameraCommandPayload
{
    [JsonPropertyName("action")]
    public string Action { get; init; } = string.Empty;

    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [JsonPropertyName("numericValue")]
    public double? NumericValue { get; init; }

    [JsonPropertyName("x")]
    public double? X { get; init; }

    [JsonPropertyName("y")]
    public double? Y { get; init; }
}

public sealed record DecodedVideoFrame(
    string DeviceId,
    int Width,
    int Height,
    byte[] Bgr24Data,
    DateTimeOffset Timestamp);

public sealed record DecodedAudioSamples(
    string DeviceId,
    int SampleRate,
    short[] Pcm16Samples);
