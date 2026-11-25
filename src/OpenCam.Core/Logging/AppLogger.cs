using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenCam.Core.Logging;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error
}

public sealed record LogEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Category,
    string Message);

public sealed partial class AppLogger : IDisposable
{
    private readonly string _logDirectory;
    private readonly int _maxMemoryEntries;
    private readonly long _maxFileBytes;
    private readonly object _fileLock = new();
    private readonly ConcurrentQueue<LogEntry> _ringBuffer = new();
    private int _approxCount;
    private bool _disposed;

    public string LogDirectory => _logDirectory;

    public event Action<LogEntry>? EntryAdded;

    public AppLogger(
        string? logDirectory = null,
        int maxMemoryEntries = 2000,
        long maxFileBytes = 5 * 1024 * 1024)
    {
        _logDirectory = logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenCam",
            "Logs");
        _maxMemoryEntries = Math.Max(50, maxMemoryEntries);
        _maxFileBytes = Math.Max(16 * 1024, maxFileBytes);
        Directory.CreateDirectory(_logDirectory);
    }

    public void Debug(string category, string message) => Log(LogLevel.Debug, category, message);
    public void Info(string category, string message) => Log(LogLevel.Info, category, message);
    public void Warn(string category, string message) => Log(LogLevel.Warn, category, message);
    public void Error(string category, string message, Exception? ex = null)
    {
        var full = ex is null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}";
        Log(LogLevel.Error, category, full);
    }

    public void Log(LogLevel level, string category, string message)
    {
        if (_disposed) return;

        var redacted = RedactSecrets(message);
        var entry = new LogEntry(DateTimeOffset.Now, level, category, redacted);

        _ringBuffer.Enqueue(entry);
        var count = Interlocked.Increment(ref _approxCount);
        while (count > _maxMemoryEntries && _ringBuffer.TryDequeue(out _))
        {
            count = Interlocked.Decrement(ref _approxCount);
        }

        WriteToRollingFile(entry);
        EntryAdded?.Invoke(entry);
    }

    public IReadOnlyList<LogEntry> GetEntries() => _ringBuffer.ToArray();

    public string ExportAsText()
    {
        var sb = new StringBuilder();
        foreach (var e in _ringBuffer.ToArray())
        {
            sb.AppendLine($"[{e.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{e.Level,-5}] [{e.Category}] {e.Message}");
        }
        return sb.ToString();
    }

    public void ClearMemory()
    {
        while (_ringBuffer.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _approxCount);
        }
    }

    public static string RedactSecrets(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var result = TokenParamRegex().Replace(input, "$1[REDACTED]");
        result = BearerRegex().Replace(result, "$1[REDACTED]");
        result = JsonSecretRegex().Replace(result, "$1\"[REDACTED]\"");
        return result;
    }

    private void WriteToRollingFile(LogEntry entry)
    {
        var line = $"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{entry.Level,-5}] [{entry.Category}] {entry.Message}{Environment.NewLine}";
        lock (_fileLock)
        {
            try
            {
                var currentPath = Path.Combine(_logDirectory, $"opencam-{DateTime.Now:yyyyMMdd}.log");
                if (File.Exists(currentPath) && new FileInfo(currentPath).Length >= _maxFileBytes)
                {
                    var archivePath = Path.Combine(
                        _logDirectory,
                        $"opencam-{DateTime.Now:yyyyMMdd-HHmmssfff}.log");
                    File.Move(currentPath, archivePath, overwrite: true);
                    PruneOldLogs();
                }

                File.AppendAllText(currentPath, line, Encoding.UTF8);
            }
            catch
            {
                // Logging must never crash the application.
            }
        }
    }

    private void PruneOldLogs()
    {
        try
        {
            var files = new DirectoryInfo(_logDirectory)
                .GetFiles("opencam-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(10);
            foreach (var f in files)
            {
                f.Delete();
            }
        }
        catch
        {
            // Ignore cleanup errors.
        }
    }

    [GeneratedRegex(@"(?i)((?:token|pin|secret|password|auth|key)=)([^\s&;,]+)")]
    private static partial Regex TokenParamRegex();

    [GeneratedRegex(@"(?i)(Bearer\s+)([A-Za-z0-9\-._~+/]+=*)")]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"(?i)(""(?:token|pin|secret|password)""\s*:\s*)(""[^""]*"")")]
    private static partial Regex JsonSecretRegex();

    public void Dispose()
    {
        _disposed = true;
    }
}
