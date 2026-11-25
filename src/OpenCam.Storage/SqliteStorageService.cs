using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenCam.Core.Models;

namespace OpenCam.Storage;

public sealed record PairedDeviceRecord(
    string DeviceId,
    string Name,
    string UserAgent,
    string LastIpAddress,
    DateTimeOffset FirstPairedAt,
    DateTimeOffset LastSeenAt,
    bool IsRevoked);

public sealed record AuditEventRecord(
    long Id,
    DateTimeOffset Timestamp,
    string EventType,
    string? DeviceId,
    string Details);

public sealed class SqliteStorageService : IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _dbLock = new(1, 1);

    public string DatabasePath { get; }

    public SqliteStorageService(string? databasePath = null)
    {
        DatabasePath = databasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenCam",
            "opencam.db");

        var dir = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS app_settings (
                key TEXT PRIMARY KEY,
                value_json TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS paired_devices (
                device_id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                user_agent TEXT NOT NULL,
                last_ip TEXT NOT NULL,
                first_paired_at TEXT NOT NULL,
                last_seen_at TEXT NOT NULL,
                is_revoked INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS audit_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL,
                event_type TEXT NOT NULL,
                device_id TEXT NULL,
                details TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public async Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default)
    {
        await _dbLock.WaitAsync(ct);
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value_json FROM app_settings WHERE key = 'main'";
            var raw = await cmd.ExecuteScalarAsync(ct) as string;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new AppSettings();
            }

            return JsonSerializer.Deserialize<AppSettings>(raw) ?? new AppSettings();
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var json = JsonSerializer.Serialize(settings);
        await _dbLock.WaitAsync(ct);
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO app_settings (key, value_json, updated_at)
                VALUES ('main', $json, $now)
                ON CONFLICT(key) DO UPDATE SET
                    value_json = excluded.value_json,
                    updated_at = excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("$json", json);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task UpsertDeviceAsync(PairedDeviceRecord device, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        await _dbLock.WaitAsync(ct);
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO paired_devices (device_id, name, user_agent, last_ip, first_paired_at, last_seen_at, is_revoked)
                VALUES ($id, $name, $ua, $ip, $first, $last, $revoked)
                ON CONFLICT(device_id) DO UPDATE SET
                    name = excluded.name,
                    user_agent = excluded.user_agent,
                    last_ip = excluded.last_ip,
                    last_seen_at = excluded.last_seen_at,
                    is_revoked = excluded.is_revoked;
                """;
            cmd.Parameters.AddWithValue("$id", device.DeviceId);
            cmd.Parameters.AddWithValue("$name", device.Name);
            cmd.Parameters.AddWithValue("$ua", device.UserAgent);
            cmd.Parameters.AddWithValue("$ip", device.LastIpAddress);
            cmd.Parameters.AddWithValue("$first", device.FirstPairedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$last", device.LastSeenAt.ToString("O"));
            cmd.Parameters.AddWithValue("$revoked", device.IsRevoked ? 1 : 0);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task SetDeviceRevokedAsync(string deviceId, bool revoked, CancellationToken ct = default)
    {
        await _dbLock.WaitAsync(ct);
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE paired_devices SET is_revoked = $revoked WHERE device_id = $id;";
            cmd.Parameters.AddWithValue("$revoked", revoked ? 1 : 0);
            cmd.Parameters.AddWithValue("$id", deviceId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<IReadOnlyList<PairedDeviceRecord>> GetDevicesAsync(CancellationToken ct = default)
    {
        await _dbLock.WaitAsync(ct);
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT device_id, name, user_agent, last_ip, first_paired_at, last_seen_at, is_revoked
                FROM paired_devices
                ORDER BY last_seen_at DESC;
                """;
            var list = new List<PairedDeviceRecord>();
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new PairedDeviceRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    DateTimeOffset.Parse(reader.GetString(4)),
                    DateTimeOffset.Parse(reader.GetString(5)),
                    reader.GetInt32(6) != 0));
            }
            return list;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task AppendAuditEventAsync(string eventType, string? deviceId, string details, CancellationToken ct = default)
    {
        await _dbLock.WaitAsync(ct);
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO audit_events (timestamp, event_type, device_id, details)
                VALUES ($ts, $type, $dev, $details);
                """;
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$type", eventType);
            cmd.Parameters.AddWithValue("$dev", (object?)deviceId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$details", details);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<IReadOnlyList<AuditEventRecord>> GetRecentAuditEventsAsync(int limit = 200, CancellationToken ct = default)
    {
        await _dbLock.WaitAsync(ct);
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, timestamp, event_type, device_id, details
                FROM audit_events
                ORDER BY id DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", limit);
            var list = new List<AuditEventRecord>();
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new AuditEventRecord(
                    reader.GetInt64(0),
                    DateTimeOffset.Parse(reader.GetString(1)),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4)));
            }
            return list;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public void Dispose()
    {
        _dbLock.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
