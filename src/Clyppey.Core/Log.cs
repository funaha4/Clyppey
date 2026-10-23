using System.Text;

namespace Clyppey.Core;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error
}

public sealed record LogEntry(DateTime Timestamp, LogLevel Level, string Message);

/// <summary>
/// Tiny thread-safe logger: keeps the recent entries in memory (for the diagnostics page)
/// and appends to a rotating file in %LOCALAPPDATA%\Clyppey.
/// </summary>
public static class Log
{
    private const int MaxMemoryEntries = 500;
    private const long MaxFileBytes = 4 * 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly Queue<LogEntry> Recent = new();
    private static StreamWriter? _writer;

    static Log()
    {
        try
        {
            AppPaths.EnsureDirectories();
            RotateIfNeeded();
            _writer = new StreamWriter(new FileStream(
                AppPaths.LogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                // BOM so Windows tools that guess ANSI still render Japanese correctly.
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)) { AutoFlush = true };
        }
        catch (Exception)
        {
            _writer = null; // Logging must never take the app down.
        }
    }

    /// <summary>Raised on every entry; used by the UI to stream the log view.</summary>
    public static event Action<LogEntry>? Written;

    public static void Debug(string message) => Write(LogLevel.Debug, message);

    public static void Info(string message) => Write(LogLevel.Info, message);

    public static void Warn(string message) => Write(LogLevel.Warn, message);

    public static void Error(string message) => Write(LogLevel.Error, message);

    public static void Error(string message, Exception ex) =>
        Write(LogLevel.Error, $"{message}: {ex.GetType().Name}: {ex.Message}");

    public static IReadOnlyList<LogEntry> Snapshot()
    {
        lock (Gate)
        {
            return Recent.ToArray();
        }
    }

    private static void Write(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        lock (Gate)
        {
            Recent.Enqueue(entry);
            while (Recent.Count > MaxMemoryEntries)
            {
                Recent.Dequeue();
            }

            try
            {
                _writer?.WriteLine($"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{level,-5}] {message}");
            }
            catch (Exception)
            {
                // Ignore write failures (disk full, file locked...).
            }
        }

        try
        {
            Written?.Invoke(entry);
        }
        catch (Exception)
        {
            // Subscriber failures are not our problem.
        }
    }

    private static void RotateIfNeeded()
    {
        var file = new FileInfo(AppPaths.LogFile);
        if (file.Exists && file.Length > MaxFileBytes)
        {
            var backup = AppPaths.LogFile + ".1";
            File.Delete(backup);
            File.Move(AppPaths.LogFile, backup);
        }
    }
}
