using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using OpenCam.Core.Logging;
using OpenCam.Core.Models;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;

namespace OpenCam.WebRTC;

public sealed class WebRtcPeerSession : IDisposable
{
    private readonly AppLogger _logger;
    private readonly VpxVideoEncoder _videoDecoder = new();
    private readonly AudioEncoder _audioDecoder = new();
    private readonly ConcurrentQueue<RTCIceCandidateInit> _pendingIceCandidates = new();
    private readonly object _stateLock = new();

    private RTCPeerConnection? _pc;
    private RTCDataChannel? _controlChannel;
    private volatile bool _remoteDescriptionSet;
    private volatile bool _disposed;

    private long _bytesReceivedWindow;
    private int _framesReceivedWindow;
    private long _lastStatsTick = Stopwatch.GetTimestamp();
    private int _lastWidth;
    private int _lastHeight;
    private double _lastFps;
    private double _lastBitrateKbps;
    private double _lastLatencyMs;
    private string _negotiatedCodec = "VP8";
    private VideoCodecsEnum _activeCodecEnum = VideoCodecsEnum.VP8;
    private AudioCodecsEnum _activeAudioCodec = AudioCodecsEnum.PCMU;
    private int _loggedFirstVideoRtp;
    private int _loggedFirstVideoFrame;
    private int _loggedFirstDecodedFrame;

    public string DeviceId { get; }
    public string DeviceName { get; }
    public DeviceConnectionState ConnectionState { get; private set; } = DeviceConnectionState.Connecting;

    public event Action<string, IceCandidatePayload>? LocalIceCandidateReady;
    public event Action<string, DeviceConnectionState>? ConnectionStateChanged;
    public event Action<DecodedVideoFrame>? VideoFrameDecoded;
    public event Action<DecodedAudioSamples>? AudioSamplesDecoded;
    public event Action<string, StreamTelemetry>? TelemetryUpdated;

    public WebRtcPeerSession(string deviceId, string deviceName, AppLogger logger)
    {
        DeviceId = deviceId;
        DeviceName = deviceName;
        _logger = logger;
    }

    public int PendingIceCandidateCount => _pendingIceCandidates.Count;
    public bool IsRemoteDescriptionSet => _remoteDescriptionSet;

    public async Task<string> AcceptOfferAndCreateAnswerAsync(string offerSdp, IPAddress? bindAddress = null)
    {
        ThrowIfDisposed();

        var config = new RTCConfiguration
        {
            iceServers =
            [
                new RTCIceServer { urls = "stun:stun.l.google.com:19302" },
                new RTCIceServer { urls = "stun:stun.cloudflare.com:3478" },
                new RTCIceServer
                {
                    urls = "turn:openrelay.metered.ca:443?transport=tcp",
                    username = "openrelayproject",
                    credential = "openrelayproject"
                }
            ],
            X_BindAddress = bindAddress
        };

        var pc = new RTCPeerConnection(config);
        _pc = pc;

        var videoFormats = new List<VideoFormat>
        {
            new(VideoCodecsEnum.VP8, 96, 90000)
        };
        var videoTrack = new MediaStreamTrack(videoFormats, MediaStreamStatusEnum.RecvOnly);
        pc.addTrack(videoTrack);

        if (offerSdp.Contains("m=audio", StringComparison.Ordinal))
        {
            var audioFormats = new List<AudioFormat>
            {
                new(AudioCodecsEnum.OPUS, 111, 48000, 2),
                new(AudioCodecsEnum.PCMU, 0, 8000, 1)
            };
            var audioTrack = new MediaStreamTrack(audioFormats, MediaStreamStatusEnum.RecvOnly);
            pc.addTrack(audioTrack);
        }

        pc.OnVideoFormatsNegotiated += formats =>
        {
            if (formats.Count > 0)
            {
                _activeCodecEnum = formats[0].Codec;
                _negotiatedCodec = formats[0].Codec.ToString();
                _logger.Info("WebRTC", $"[{DeviceId}] Negotiated video codec: {_negotiatedCodec}");
            }
        };

        pc.OnAudioFormatsNegotiated += formats =>
        {
            if (formats.Count > 0)
            {
                _activeAudioCodec = formats[0].Codec;
                _logger.Info("WebRTC", $"[{DeviceId}] Negotiated audio codec: {_activeAudioCodec}");
            }
        };

        pc.onicecandidate += candidate =>
        {
            if (_disposed || candidate is null) return;
            _logger.Debug("WebRTC", $"[{DeviceId}] Local ICE candidate ({candidate.type}): {candidate.candidate}");
            LocalIceCandidateReady?.Invoke(DeviceId, new IceCandidatePayload
            {
                Candidate = candidate.candidate,
                SdpMid = candidate.sdpMid,
                sdpMLineIndex = candidate.sdpMLineIndex
            });
        };

        pc.oniceconnectionstatechange += iceState =>
        {
            _logger.Info("WebRTC", $"[{DeviceId}] ICE state -> {iceState}");
            if (iceState == RTCIceConnectionState.connected)
            {
                UpdateState(DeviceConnectionState.Streaming);
            }
            else if (iceState == RTCIceConnectionState.disconnected)
            {
                // Do not abort on transient disconnected state per reliability rules.
                UpdateState(DeviceConnectionState.Reconnecting);
            }
            else if (iceState is RTCIceConnectionState.failed or RTCIceConnectionState.closed)
            {
                UpdateState(DeviceConnectionState.Disconnected);
            }
        };

        pc.onconnectionstatechange += state =>
        {
            _logger.Info("WebRTC", $"[{DeviceId}] PeerConnection state -> {state}");
            if (state == RTCPeerConnectionState.connected)
            {
                UpdateState(DeviceConnectionState.Streaming);
            }
            else if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)
            {
                UpdateState(DeviceConnectionState.Disconnected);
            }
        };

        pc.OnVideoFrameReceived += OnEncodedVideoFrameReceived;
        pc.OnRtpPacketReceived += OnRtpPacketReceived;
        pc.ondatachannel += dc =>
        {
            _controlChannel = dc;
            _logger.Debug("WebRTC", $"[{DeviceId}] DataChannel attached: {dc.label}");
        };

        var setResult = pc.setRemoteDescription(new RTCSessionDescriptionInit
        {
            type = RTCSdpType.offer,
            sdp = offerSdp
        });

        if (setResult != SetDescriptionResultEnum.OK)
        {
            throw new InvalidOperationException($"Failed to set remote offer SDP: {setResult}");
        }

        _remoteDescriptionSet = true;
        DrainPendingIceCandidates();

        var answer = pc.createAnswer();
        await pc.setLocalDescription(answer);

        _logger.Info("WebRTC", $"[{DeviceId}] Created SDP answer for {DeviceName}");
        return answer.sdp;
    }

    public void AddRemoteIceCandidate(IceCandidatePayload payload)
    {
        if (_disposed || string.IsNullOrWhiteSpace(payload.Candidate)) return;

        var init = new RTCIceCandidateInit
        {
            candidate = payload.Candidate,
            sdpMid = payload.SdpMid,
            sdpMLineIndex = payload.sdpMLineIndex
        };

        if (!_remoteDescriptionSet || _pc is null)
        {
            _pendingIceCandidates.Enqueue(init);
            _logger.Debug("WebRTC", $"[{DeviceId}] Queued early ICE candidate (queue size: {_pendingIceCandidates.Count})");
            return;
        }

        _pc.addIceCandidate(init);
        _logger.Debug("WebRTC", $"[{DeviceId}] Added remote ICE candidate: {payload.Candidate}");
    }

    public void UpdateClientTelemetry(ClientTelemetryPayload payload)
    {
        if (payload.SentTimestampMs > 0)
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var rttEstimate = Math.Max(1.0, nowMs - payload.SentTimestampMs);
            _lastLatencyMs = Math.Min(rttEstimate, 2000.0);
        }

        EmitTelemetry(payload.AudioEnabled, payload.BatteryPercent);
    }

    private void DrainPendingIceCandidates()
    {
        if (_pc is null) return;
        var drained = 0;
        while (_pendingIceCandidates.TryDequeue(out var candidate))
        {
            _pc.addIceCandidate(candidate);
            drained++;
        }

        if (drained > 0)
        {
            _logger.Info("WebRTC", $"[{DeviceId}] Drained {drained} queued early ICE candidate(s)");
        }
    }

    private void OnEncodedVideoFrameReceived(IPEndPoint remoteEp, uint timestamp, byte[] frame, VideoFormat format)
    {
        if (_disposed || frame.Length == 0) return;

        if (Interlocked.Exchange(ref _loggedFirstVideoFrame, 1) == 0)
        {
            _logger.Info("WebRTC", $"[{DeviceId}] First encoded video frame received ({frame.Length} bytes, codec={format.Codec})");
        }

        Interlocked.Add(ref _bytesReceivedWindow, frame.Length);
        Interlocked.Increment(ref _framesReceivedWindow);

        try
        {
            var decoded = _videoDecoder.DecodeVideo(frame, VideoPixelFormatsEnum.Bgr, _activeCodecEnum);
            foreach (var sample in decoded)
            {
                if (sample.Sample is null || sample.Width <= 0 || sample.Height <= 0) continue;

                var rawW = (int)sample.Width;
                var rawH = (int)sample.Height;
                var alignedW = rawW & ~3; // 4-byte aligned width prevents DIB/WriteableBitmap diagonal shear
                if (alignedW <= 0) continue;

                var actualSrcStride = sample.Sample.Length / rawH;
                if (actualSrcStride < rawW * 3) actualSrcStride = rawW * 3;

                byte[] cleanBgr;
                if (alignedW == rawW && actualSrcStride == rawW * 3)
                {
                    cleanBgr = sample.Sample;
                }
                else
                {
                    var dstStride = alignedW * 3;
                    cleanBgr = new byte[dstStride * rawH];
                    for (var y = 0; y < rawH; y++)
                    {
                        Buffer.BlockCopy(sample.Sample, y * actualSrcStride, cleanBgr, y * dstStride, dstStride);
                    }
                }

                _lastWidth = alignedW;
                _lastHeight = rawH;

                if (Interlocked.Exchange(ref _loggedFirstDecodedFrame, 1) == 0)
                {
                    _logger.Info("WebRTC", $"[{DeviceId}] First decoded video frame: {_lastWidth}x{_lastHeight} (raw {rawW}x{rawH}, srcStride={actualSrcStride})");
                }

                VideoFrameDecoded?.Invoke(new DecodedVideoFrame(
                    DeviceId,
                    _lastWidth,
                    _lastHeight,
                    cleanBgr,
                    DateTimeOffset.UtcNow));
            }
        }
        catch (Exception ex)
        {
            _logger.Debug("WebRTC", $"[{DeviceId}] Frame decode skipped: {ex.Message}");
        }

        MaybeRollTelemetryWindow();
    }

    private void OnRtpPacketReceived(IPEndPoint remoteEp, SDPMediaTypesEnum mediaType, RTPPacket rtpPacket)
    {
        if (_disposed) return;
        if (mediaType == SDPMediaTypesEnum.video)
        {
            if (Interlocked.Exchange(ref _loggedFirstVideoRtp, 1) == 0)
            {
                _logger.Info("WebRTC", $"[{DeviceId}] First video RTP packet from {remoteEp} (pt={rtpPacket.Header.PayloadType}, len={rtpPacket.Payload?.Length ?? 0})");
            }
            return;
        }
        if (mediaType != SDPMediaTypesEnum.audio) return;
        var payload = rtpPacket.Payload;
        if (payload is null || payload.Length == 0) return;

        try
        {
            var audioFormat = new AudioFormat(_activeAudioCodec, rtpPacket.Header.PayloadType, _activeAudioCodec == AudioCodecsEnum.OPUS ? 48000 : 8000);
            var pcm = _audioDecoder.DecodeAudio(payload, audioFormat);
            if (pcm is { Length: > 0 })
            {
                AudioSamplesDecoded?.Invoke(new DecodedAudioSamples(DeviceId, audioFormat.ClockRate, pcm));
            }
        }
        catch
        {
            // Ignore malformed/unsupported comfort noise frames.
        }
    }

    private void MaybeRollTelemetryWindow()
    {
        var nowTick = Stopwatch.GetTimestamp();
        var elapsedSec = (double)(nowTick - _lastStatsTick) / Stopwatch.Frequency;
        if (elapsedSec < 1.0) return;

        var bytes = Interlocked.Exchange(ref _bytesReceivedWindow, 0);
        var frames = Interlocked.Exchange(ref _framesReceivedWindow, 0);
        _lastStatsTick = nowTick;

        _lastFps = Math.Round(frames / elapsedSec, 1);
        _lastBitrateKbps = Math.Round((bytes * 8.0 / 1000.0) / elapsedSec, 0);

        EmitTelemetry(audioEnabled: false, batteryPercent: null);
    }

    private void EmitTelemetry(bool audioEnabled, int? batteryPercent)
    {
        var quality = _lastFps switch
        {
            >= 24 when _lastLatencyMs < 80 => "Excellent",
            >= 15 when _lastLatencyMs < 150 => "Good",
            >= 8 => "Fair",
            _ => "Poor"
        };

        TelemetryUpdated?.Invoke(DeviceId, new StreamTelemetry
        {
            DeviceId = DeviceId,
            Width = _lastWidth,
            Height = _lastHeight,
            Fps = _lastFps,
            BitrateKbps = _lastBitrateKbps,
            LatencyMs = _lastLatencyMs,
            Codec = _negotiatedCodec,
            QualityLabel = quality,
            AudioEnabled = audioEnabled,
            BatteryPercent = batteryPercent,
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    private void UpdateState(DeviceConnectionState next)
    {
        lock (_stateLock)
        {
            if (ConnectionState == next) return;
            ConnectionState = next;
        }
        ConnectionStateChanged?.Invoke(DeviceId, next);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _remoteDescriptionSet = false;

        // Null out references BEFORE calling close() to prevent onclose re-entrancy stack overflows.
        var dc = Interlocked.Exchange(ref _controlChannel, null);
        try { dc?.close(); } catch { }

        var pc = Interlocked.Exchange(ref _pc, null);
        try { pc?.close(); } catch { }
        try { pc?.Dispose(); } catch { }

        _videoDecoder.Dispose();
    }
}
