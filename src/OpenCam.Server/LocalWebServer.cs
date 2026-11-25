using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OpenCam.Core.Logging;
using OpenCam.Core.Models;
using OpenCam.Security;
using OpenCam.Storage;
using OpenCam.WebRTC;

namespace OpenCam.Server;

public sealed class LocalWebServer : IAsyncDisposable
{
    private readonly AppLogger _logger;
    private readonly PairingAuthService _authService;
    private readonly LanCertificateService _certService;
    private readonly SqliteStorageService _storage;
    private readonly WebRtcSessionManager _webRtcManager;

    private readonly ConcurrentDictionary<string, WebSocket> _deviceSockets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConnectedDevice> _connectedDevices = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<IceCandidatePayload>> _pendingOutgoingIce = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _answerSent = new(StringComparer.Ordinal);

    private WebApplication? _app;
    private CancellationTokenSource? _cts;
    private bool _isRunning;

    public bool IsRunning => _isRunning;
    public string? BoundIpAddress { get; private set; }
    public int BoundPort { get; private set; }
    public string? PairingUrl { get; private set; }

    public event Action<ConnectedDevice>? DeviceConnected;
    public event Action<string>? DeviceDisconnected;
    public event Action<ConnectedDevice>? DeviceUpdated;

    public LocalWebServer(
        AppLogger logger,
        PairingAuthService authService,
        LanCertificateService certService,
        SqliteStorageService storage,
        WebRtcSessionManager webRtcManager)
    {
        _logger = logger;
        _authService = authService;
        _certService = certService;
        _storage = storage;
        _webRtcManager = webRtcManager;

        _webRtcManager.LocalIceCandidateReady += OnLocalIceCandidateReady;
        _webRtcManager.DeviceStateChanged += OnDeviceStateChanged;
        _webRtcManager.TelemetryUpdated += OnTelemetryUpdated;
    }

    public IReadOnlyList<ConnectedDevice> GetConnectedDevices() => _connectedDevices.Values.ToArray();

    public async Task StartAsync(string lanIpAddress, int port, AppSettings settings, CancellationToken ct = default)
    {
        if (_isRunning) return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        BoundIpAddress = lanIpAddress;
        BoundPort = port;

        var pairingToken = _authService.RotateMasterPairingToken(settings.SessionExpirationMinutes);
        PairingUrl = $"https://{lanIpAddress}:{port}/?token={pairingToken}";

        X509Certificate2 cert = _certService.GetOrCreateLanCertificate(lanIpAddress);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Any, port, listen =>
            {
                listen.UseHttps(cert);
            });
        });

        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(15)
        });

        app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "OpenCam" }));

        app.MapGet("/cert", () =>
        {
            if (!File.Exists(_certService.PublicCertPath))
            {
                return Results.NotFound();
            }
            var bytes = File.ReadAllBytes(_certService.PublicCertPath);
            return Results.File(bytes, "application/x-x509-ca-cert", "opencam-lan.cer");
        });

        app.Map("/ws", HandleWebSocketAsync);

        app.MapGet("/", async context =>
        {
            await ServeEmbeddedStaticFileAsync(context, "/index.html");
        });

        app.MapGet("/{**path}", async context =>
        {
            var path = context.Request.Path.Value ?? "/index.html";
            await ServeEmbeddedStaticFileAsync(context, path);
        });

        _app = app;
        await app.StartAsync(_cts.Token);
        _isRunning = true;

        _logger.Info("Server", $"OpenCam HTTPS/WSS server listening on https://{lanIpAddress}:{port}");
    }

    public async Task StopAsync()
    {
        if (!_isRunning) return;
        _isRunning = false;

        foreach (var kv in _deviceSockets)
        {
            if (_deviceSockets.TryRemove(kv.Key, out var ws))
            {
                try
                {
                    if (ws.State == WebSocketState.Open)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server stopping", CancellationToken.None);
                    }
                    ws.Dispose();
                }
                catch { }
            }
        }

        foreach (var devId in _connectedDevices.Keys.ToArray())
        {
            _webRtcManager.RemoveSession(devId);
            if (_connectedDevices.TryRemove(devId, out _))
            {
                DeviceDisconnected?.Invoke(devId);
            }
        }

        if (_app is not null)
        {
            try
            {
                await _app.StopAsync();
                await _app.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.Warn("Server", $"Error stopping Kestrel: {ex.Message}");
            }
            _app = null;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        _logger.Info("Server", "OpenCam HTTPS/WSS server stopped.");
    }

    public async Task SendCommandAsync(string deviceId, CameraCommandPayload command)
    {
        if (!_deviceSockets.TryGetValue(deviceId, out var ws) || ws.State != WebSocketState.Open) return;
        try
        {
            await SendJsonAsync(ws, new SignalingMessage
            {
                Type = "command",
                DeviceId = deviceId,
                Command = command
            });
        }
        catch (Exception ex)
        {
            _logger.Debug("WS", $"Failed to send command to {deviceId}: {ex.Message}");
        }
    }

    public async Task DisconnectAndRevokeDeviceAsync(string deviceId)
    {
        _authService.RevokeDevice(deviceId);
        await _storage.SetDeviceRevokedAsync(deviceId, true);
        await _storage.AppendAuditEventAsync("DEVICE_REVOKED", deviceId, "Device disconnected and access revoked");

        if (_deviceSockets.TryRemove(deviceId, out var ws))
        {
            try
            {
                await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Access revoked", CancellationToken.None);
            }
            catch { }
        }

        _webRtcManager.RemoveSession(deviceId);
        if (_connectedDevices.TryRemove(deviceId, out _))
        {
            DeviceDisconnected?.Invoke(deviceId);
        }
    }

    private async Task HandleWebSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var token = context.Request.Query["token"].ToString();
        var pin = context.Request.Query["pin"].ToString();
        var settings = await _storage.LoadSettingsAsync();

        if (!_authService.ValidatePairingRequest(token, pin, settings.RequirePin, settings.PairingPin))
        {
            _logger.Warn("Auth", $"Rejected unauthorized WebSocket connection from {context.Connection.RemoteIpAddress}");
            using var rejectedWs = await context.WebSockets.AcceptWebSocketAsync();
            var errMsg = settings.RequirePin ? "Invalid or missing pairing PIN." : "Invalid or expired pairing token.";
            await SendJsonAsync(rejectedWs, new SignalingMessage { Type = "error", Error = errMsg });
            await rejectedWs.CloseAsync(WebSocketCloseStatus.PolicyViolation, errMsg, CancellationToken.None);
            return;
        }

        using var ws = await context.WebSockets.AcceptWebSocketAsync();
        string? boundDeviceId = null;
        var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var userAgent = context.Request.Headers.UserAgent.ToString();

        var buffer = new byte[64 * 1024];
        try
        {
            while (ws.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, context.RequestAborted);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var json = Encoding.UTF8.GetString(ms.ToArray());
                var msg = JsonSerializer.Deserialize<SignalingMessage>(json);
                if (msg is null) continue;

                if (!string.IsNullOrWhiteSpace(msg.DeviceId))
                {
                    boundDeviceId ??= msg.DeviceId;
                }

                await ProcessSignalingMessageAsync(msg, ws, remoteIp, userAgent);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (WebSocketException ex)
        {
            _logger.Debug("WS", $"WebSocket closed ({boundDeviceId ?? "unknown"}): {ex.Message}");
        }
        finally
        {
            if (boundDeviceId is not null)
            {
                _deviceSockets.TryRemove(boundDeviceId, out _);
                _webRtcManager.RemoveSession(boundDeviceId);
                if (_connectedDevices.TryRemove(boundDeviceId, out _))
                {
                    DeviceDisconnected?.Invoke(boundDeviceId);
                }
                _logger.Info("WS", $"Device disconnected: {boundDeviceId}");
            }
        }
    }

    private async Task ProcessSignalingMessageAsync(
        SignalingMessage msg,
        WebSocket ws,
        string remoteIp,
        string userAgent)
    {
        var deviceId = msg.DeviceId;
        if (string.IsNullOrWhiteSpace(deviceId)) return;

        switch (msg.Type)
        {
            case "offer":
                if (string.IsNullOrWhiteSpace(msg.Sdp)) return;

                _deviceSockets[deviceId] = ws;
                var deviceName = string.IsNullOrWhiteSpace(msg.DeviceName) ? "Mobile Camera" : msg.DeviceName;

                var device = new ConnectedDevice
                {
                    DeviceId = deviceId,
                    Name = deviceName,
                    UserAgent = userAgent,
                    IpAddress = remoteIp,
                    State = DeviceConnectionState.Connecting,
                    IsActiveVirtualCamSource = _webRtcManager.ActiveVirtualCamDeviceId == deviceId || _connectedDevices.IsEmpty
                };
                _connectedDevices[deviceId] = device;
                DeviceConnected?.Invoke(device);

                var now = DateTimeOffset.UtcNow;
                await _storage.UpsertDeviceAsync(new PairedDeviceRecord(
                    deviceId, deviceName, userAgent, remoteIp, now, now, false));
                await _storage.AppendAuditEventAsync("DEVICE_CONNECTED", deviceId, $"Connected from {remoteIp}");

                _answerSent[deviceId] = false;
                _pendingOutgoingIce[deviceId] = new ConcurrentQueue<IceCandidatePayload>();

                var session = _webRtcManager.CreateOrReplaceSession(deviceId, deviceName);
                IPAddress.TryParse(BoundIpAddress, out var bindIp);
                var answerSdp = await session.AcceptOfferAndCreateAnswerAsync(msg.Sdp, bindIp);

                await SendJsonAsync(ws, new SignalingMessage
                {
                    Type = "answer",
                    DeviceId = deviceId,
                    Sdp = answerSdp
                });

                _answerSent[deviceId] = true;
                if (_pendingOutgoingIce.TryGetValue(deviceId, out var iceQueue))
                {
                    while (iceQueue.TryDequeue(out var queued))
                    {
                        await SendJsonAsync(ws, new SignalingMessage
                        {
                            Type = "ice-candidate",
                            DeviceId = deviceId,
                            Candidate = queued
                        });
                    }
                }
                break;

            case "ice-candidate":
                if (msg.Candidate is not null && _webRtcManager.TryGetSession(deviceId, out var peer) && peer is not null)
                {
                    peer.AddRemoteIceCandidate(msg.Candidate);
                }
                break;

            case "telemetry":
                if (msg.Telemetry is not null && _webRtcManager.TryGetSession(deviceId, out var tPeer) && tPeer is not null)
                {
                    tPeer.UpdateClientTelemetry(msg.Telemetry);
                }
                break;

            case "client-log":
                if (!string.IsNullOrWhiteSpace(msg.Error))
                {
                    _logger.Info("Mobile", $"[{deviceId}] {msg.Error}");
                }
                break;
        }
    }

    private async void OnLocalIceCandidateReady(string deviceId, IceCandidatePayload candidate)
    {
        if (!_answerSent.TryGetValue(deviceId, out var sent) || !sent)
        {
            var q = _pendingOutgoingIce.GetOrAdd(deviceId, _ => new ConcurrentQueue<IceCandidatePayload>());
            q.Enqueue(candidate);
            return;
        }

        if (!_deviceSockets.TryGetValue(deviceId, out var ws) || ws.State != WebSocketState.Open) return;
        try
        {
            await SendJsonAsync(ws, new SignalingMessage
            {
                Type = "ice-candidate",
                DeviceId = deviceId,
                Candidate = candidate
            });
        }
        catch
        {
            // Ignore send failures during teardown.
        }
    }

    private void OnDeviceStateChanged(string deviceId, DeviceConnectionState state)
    {
        if (_connectedDevices.TryGetValue(deviceId, out var dev))
        {
            dev.State = state;
            dev.LastSeenAt = DateTimeOffset.UtcNow;
            DeviceUpdated?.Invoke(dev);
        }
    }

    private void OnTelemetryUpdated(string deviceId, StreamTelemetry telemetry)
    {
        if (_connectedDevices.TryGetValue(deviceId, out var dev))
        {
            dev.Telemetry = telemetry;
            dev.LastSeenAt = DateTimeOffset.UtcNow;
            DeviceUpdated?.Invoke(dev);
        }
    }

    private static async Task SendJsonAsync(WebSocket ws, SignalingMessage message)
    {
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task ServeEmbeddedStaticFileAsync(HttpContext context, string requestPath)
    {
        var normalized = requestPath.TrimStart('/').Replace('/', '.');
        var asm = Assembly.GetExecutingAssembly();
        var resourceNames = asm.GetManifestResourceNames();

        var isAssetRequest = Path.HasExtension(requestPath) && !requestPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase);
        var match = resourceNames.FirstOrDefault(r =>
            r.EndsWith($".wwwroot.{normalized}", StringComparison.OrdinalIgnoreCase));

        if (match is null && !isAssetRequest)
        {
            match = resourceNames.FirstOrDefault(r => r.EndsWith(".wwwroot.index.html", StringComparison.OrdinalIgnoreCase));
        }

        if (match is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("OpenCam mobile UI bundle not found.");
            return;
        }

        context.Response.ContentType = GetContentType(match);
        await using var stream = asm.GetManifestResourceStream(match)!;
        await stream.CopyToAsync(context.Response.Body);
    }

    private static string GetContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "application/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".json" => "application/json",
        _ => "application/octet-stream"
    };

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
