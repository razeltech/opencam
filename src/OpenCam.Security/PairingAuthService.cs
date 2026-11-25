using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace OpenCam.Security;

public sealed record PairingSession(
    string Token,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    bool IsMasterPairingToken);

public sealed class PairingAuthService
{
    private readonly ConcurrentDictionary<string, PairingSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _revokedDeviceIds = new(StringComparer.Ordinal);
    private string _activePairingToken = string.Empty;

    public string ActivePairingToken => _activePairingToken;

    public string RotateMasterPairingToken(int expirationMinutes = 480)
    {
        var token = GenerateSecureToken();
        var now = DateTimeOffset.UtcNow;
        _activePairingToken = token;
        _sessions[token] = new PairingSession(token, now, now.AddMinutes(Math.Max(5, expirationMinutes)), true);
        return token;
    }

    public bool ValidatePairingRequest(string? token, string? pin, bool requirePin, string? expectedPin)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        if (!_sessions.TryGetValue(token, out var session)) return false;
        if (DateTimeOffset.UtcNow > session.ExpiresAt)
        {
            _sessions.TryRemove(token, out _);
            return false;
        }

        if (requirePin && !string.IsNullOrEmpty(expectedPin))
        {
            if (string.IsNullOrEmpty(pin)) return false;
            return CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(pin),
                System.Text.Encoding.UTF8.GetBytes(expectedPin));
        }

        return true;
    }

    public string IssueDeviceSessionToken(int expirationMinutes = 480)
    {
        var token = GenerateSecureToken();
        var now = DateTimeOffset.UtcNow;
        _sessions[token] = new PairingSession(token, now, now.AddMinutes(Math.Max(5, expirationMinutes)), false);
        return token;
    }

    public bool ValidateDeviceSession(string? token, string? deviceId)
    {
        if (!string.IsNullOrWhiteSpace(deviceId) && _revokedDeviceIds.ContainsKey(deviceId))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(token)) return false;
        if (!_sessions.TryGetValue(token, out var session)) return false;
        if (DateTimeOffset.UtcNow > session.ExpiresAt)
        {
            _sessions.TryRemove(token, out _);
            return false;
        }
        return true;
    }

    public void RevokeDevice(string deviceId)
    {
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            _revokedDeviceIds[deviceId] = 0;
        }
    }

    public void UnrevokeDevice(string deviceId)
    {
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            _revokedDeviceIds.TryRemove(deviceId, out _);
        }
    }

    public static string GenerateSecureToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
