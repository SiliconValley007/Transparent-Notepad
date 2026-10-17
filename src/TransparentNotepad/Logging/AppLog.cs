using System.Text;
using System.Text.RegularExpressions;

namespace TransparentNotepad.Logging;

public enum LogLevel { Debug = 0, Info = 1, Warn = 2, Error = 3 }

/// <summary>
/// Minimal structured file logger: one line per event ("time LEVEL event key=value ..."), daily files, size cap,
/// 7-day retention, thread-safe, secret redaction, and it never throws into the app.
/// </summary>
public sealed class AppLogger
{
    public const long MaxFileBytes = 5 * 1024 * 1024;
    private static readonly Regex SecretKeyName = new(@"key|token|secret|authorization|password|bearer", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SecretValue = new(@"(sk-[A-Za-z0-9_\-]{10,}|gsk_[A-Za-z0-9]{10,}|AIza[A-Za-z0-9_\-]{20,}|Bearer\s+[A-Za-z0-9._\-]{10,}|Token\s+[A-Za-z0-9]{16,})", RegexOptions.Compiled);

    private readonly string _dir; private readonly Func<DateTime> _now; private readonly LogLevel _min;
    private readonly object _gate = new();
    private string? _currentFile; private DateTime _lastCleanup = DateTime.MinValue; private bool _capNoted;

    public AppLogger(string directory, LogLevel minLevel = LogLevel.Info, Func<DateTime>? now = null)
    { _dir = directory; _min = minLevel; _now = now ?? (() => DateTime.Now); }

    public static AppLogger Default { get; } = new(System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TransparentNotepad", "logs"));

    public string Directory => _dir;

    public void Debug(string evt, params (string Key, object? Value)[] fields) => Write(LogLevel.Debug, evt, null, fields);
    public void Info(string evt, params (string Key, object? Value)[] fields) => Write(LogLevel.Info, evt, null, fields);
    public void Warn(string evt, params (string Key, object? Value)[] fields) => Write(LogLevel.Warn, evt, null, fields);
    public void Error(string evt, Exception? ex = null, params (string Key, object? Value)[] fields) => Write(LogLevel.Error, evt, ex, fields);

    internal static string Format(DateTime ts, LogLevel level, string evt, Exception? ex, (string Key, object? Value)[] fields)
    {
        var sb = new StringBuilder();
        sb.Append(ts.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz")).Append(' ').Append(level.ToString().ToUpperInvariant().PadRight(5)).Append(' ').Append(Clean(evt));
        foreach (var (k, v) in fields) sb.Append(' ').Append(Clean(k)).Append('=').Append(Value(k, v?.ToString() ?? "null"));
        if (ex != null) sb.Append(" exception=").Append(Value("exception", $"{ex.GetType().Name}: {ex.Message}"));
        return sb.ToString();
    }

    private static string Clean(string s) => Regex.Replace(s, @"[\s=]+", "_");

    private static string Value(string key, string value)
    {
        if (SecretKeyName.IsMatch(key)) return "***";
        value = SecretValue.Replace(value, "***").Replace("\r", " ").Replace("\n", " ");
        if (value.Length > 500) value = value[..500] + "...";
        return value.Length == 0 || value.IndexOfAny(new[] { ' ', '"', '=' }) >= 0 ? "\"" + value.Replace("\"", "'") + "\"" : value;
    }

    private void Write(LogLevel level, string evt, Exception? ex, (string Key, object? Value)[] fields)
    {
        if (level < _min) return;
        try
        {
            var now = _now(); var line = Format(now, level, evt, ex, fields);
            lock (_gate)
            {
                System.IO.Directory.CreateDirectory(_dir);
                var file = System.IO.Path.Combine(_dir, $"app-{now:yyyyMMdd}.log");
                if (file != _currentFile) { _currentFile = file; _capNoted = false; }
                if ((now - _lastCleanup).TotalHours >= 12) { _lastCleanup = now; Cleanup(now); }
                long size = File.Exists(file) ? new FileInfo(file).Length : 0;
                if (size > MaxFileBytes)
                {
                    if (!_capNoted) { File.AppendAllText(file, "--- log size cap reached; further events dropped for today ---\n"); _capNoted = true; }
                    return;
                }
                File.AppendAllText(file, line + "\n", new UTF8Encoding(false));
            }
        }
        catch { /* logging must never break the app */ }
    }

    private void Cleanup(DateTime now)
    {
        foreach (var f in System.IO.Directory.EnumerateFiles(_dir, "app-*.log"))
            try { if (File.GetLastWriteTime(f) < now.AddDays(-7)) File.Delete(f); } catch { }
    }
}

/// <summary>Convenience static facade over <see cref="AppLogger.Default"/>.</summary>
public static class Log
{
    public static void Debug(string evt, params (string Key, object? Value)[] f) => AppLogger.Default.Debug(evt, f);
    public static void Info(string evt, params (string Key, object? Value)[] f) => AppLogger.Default.Info(evt, f);
    public static void Warn(string evt, params (string Key, object? Value)[] f) => AppLogger.Default.Warn(evt, f);
    public static void Error(string evt, Exception? ex = null, params (string Key, object? Value)[] f) => AppLogger.Default.Error(evt, ex, f);
}
