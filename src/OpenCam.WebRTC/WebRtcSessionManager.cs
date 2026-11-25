using System.Collections.Concurrent;
using OpenCam.Core.Logging;
using OpenCam.Core.Models;

namespace OpenCam.WebRTC;

public sealed class WebRtcSessionManager : IDisposable
{
    private readonly AppLogger _logger;
    private readonly ConcurrentDictionary<string, WebRtcPeerSession> _sessions = new(StringComparer.Ordinal);
    private string? _activeVirtualCamDeviceId;

    public event Action<string, IceCandidatePayload>? LocalIceCandidateReady;
    public event Action<string, DeviceConnectionState>? DeviceStateChanged;
    public event Action<DecodedVideoFrame>? VideoFrameDecoded;
    public event Action<DecodedAudioSamples>? AudioSamplesDecoded;
    public event Action<string, StreamTelemetry>? TelemetryUpdated;
    public event Action<string?>? ActiveVirtualCamDeviceChanged;

    public WebRtcSessionManager(AppLogger logger)
    {
        _logger = logger;
    }

    public string? ActiveVirtualCamDeviceId => _activeVirtualCamDeviceId;

    public IReadOnlyCollection<string> ConnectedDeviceIds => _sessions.Keys.ToArray();

    public WebRtcPeerSession CreateOrReplaceSession(string deviceId, string deviceName)
    {
        if (_sessions.TryRemove(deviceId, out var existing))
        {
            existing.Dispose();
        }

        var session = new WebRtcPeerSession(deviceId, deviceName, _logger);
        session.LocalIceCandidateReady += (id, c) => LocalIceCandidateReady?.Invoke(id, c);
        session.ConnectionStateChanged += (id, state) => DeviceStateChanged?.Invoke(id, state);
        session.VideoFrameDecoded += frame => VideoFrameDecoded?.Invoke(frame);
        session.AudioSamplesDecoded += samples => AudioSamplesDecoded?.Invoke(samples);
        session.TelemetryUpdated += (id, stats) => TelemetryUpdated?.Invoke(id, stats);

        _sessions[deviceId] = session;

        if (_activeVirtualCamDeviceId is null)
        {
            SetActiveVirtualCamDevice(deviceId);
        }

        _logger.Info("WebRTC", $"Registered peer session for {deviceName} ({deviceId}). Active devices: {_sessions.Count}");
        return session;
    }

    public bool TryGetSession(string deviceId, out WebRtcPeerSession? session) =>
        _sessions.TryGetValue(deviceId, out session);

    public void SetActiveVirtualCamDevice(string? deviceId)
    {
        if (_activeVirtualCamDeviceId == deviceId) return;
        _activeVirtualCamDeviceId = deviceId;
        ActiveVirtualCamDeviceChanged?.Invoke(deviceId);
        _logger.Info("VirtualCam", $"Active virtual camera source switched to: {deviceId ?? "<none>"}");
    }

    public void RemoveSession(string deviceId)
    {
        if (_sessions.TryRemove(deviceId, out var session))
        {
            session.Dispose();
            _logger.Info("WebRTC", $"Removed peer session {deviceId}");
        }

        if (_activeVirtualCamDeviceId == deviceId)
        {
            var next = _sessions.Keys.FirstOrDefault();
            SetActiveVirtualCamDevice(next);
        }
    }

    public void Dispose()
    {
        foreach (var kv in _sessions)
        {
            if (_sessions.TryRemove(kv.Key, out var session))
            {
                session.Dispose();
            }
        }
    }
}
