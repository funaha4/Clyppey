using System.Diagnostics;
using System.Globalization;

namespace Clyppey.Core.Capture;

/// <summary>Live numbers reported by ffmpeg itself (authoritative, not guessed).</summary>
public sealed class CaptureProgress
{
    public long Frames { get; internal set; }

    public double Fps { get; internal set; }

    public long TotalBytes { get; internal set; }

    public long DropFrames { get; internal set; }

    public long DupFrames { get; internal set; }

    public double Speed { get; internal set; }

    public double OutTimeSeconds { get; internal set; }

    public DateTime UpdatedAt { get; internal set; }
}

/// <summary>Receives the raw fragmented-MP4 byte stream from the capture process.</summary>
public interface ICaptureSink
{
    void Write(ReadOnlySpan<byte> data);

    void Complete(string? error);
}

/// <summary>
/// Owns one ffmpeg capture process: starts it, pumps its binary stdout into a sink, and parses
/// the "-progress" reports from stderr.
/// </summary>
public sealed class CaptureSession : IDisposable
{
    private readonly Process _process;
    private readonly ICaptureSink _sink;
    private readonly CancellationTokenSource _cts = new();
    private Task? _stdoutPump;
    private Task? _stderrPump;
    private volatile bool _completed;

    private CaptureSession(Process process, ICaptureSink sink)
    {
        _process = process;
        _sink = sink;
    }

    public CaptureProgress Progress { get; } = new();

    public int ProcessId => _process.Id;

    public bool HasExited => _process.HasExited;

    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    public TimeSpan CpuTime => SafeCpuTime();

    /// <summary>Non-progress stderr lines (warnings, encoder errors).</summary>
    public event Action<string>? LogLine;

    public static CaptureSession Start(CaptureOptions options, ICaptureSink sink, bool lowPriority = true)
    {
        var args = FfmpegCommandBuilder.BuildCaptureArgs(options);
        var psi = new ProcessStartInfo
        {
            FileName = options.FfmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new InvalidOperationException("ffmpeg を起動できませんでした。");
        }

        try
        {
            // The game must always win: keep the encoder process below normal priority.
            if (lowPriority)
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"ffmpeg の優先度を下げられませんでした: {ex.Message}");
        }

        var session = new CaptureSession(process, sink);
        session.StartPumps();
        Log.Info($"キャプチャ開始: {options.Encoder.Name} {options.Fps}fps " +
                 $"{options.BitrateMbps}Mbps 出力#{options.OutputIndex} (PID {process.Id})");
        return session;
    }

    private void StartPumps()
    {
        _stdoutPump = Task.Run(() => PumpStdoutAsync(_cts.Token));
        _stderrPump = Task.Run(() => PumpStderrAsync(_cts.Token));
    }

    private async Task PumpStdoutAsync(CancellationToken ct)
    {
        var buffer = new byte[256 * 1024];
        string? error = null;
        try
        {
            var stream = _process.StandardOutput.BaseStream;
            while (!ct.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                try
                {
                    _sink.Write(buffer.AsSpan(0, read));
                }
                catch (Exception ex)
                {
                    Log.Error("キャプチャデータの処理に失敗しました", ex);
                    error = ex.Message;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on stop.
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Error("ffmpeg 出力の読み取りに失敗しました", ex);
        }
        finally
        {
            if (!_completed)
            {
                _completed = true;
                try
                {
                    _sink.Complete(error);
                }
                catch (Exception ex)
                {
                    Log.Error("キャプチャ終了処理に失敗しました", ex);
                }
            }
        }
    }

    private async Task PumpStderrAsync(CancellationToken ct)
    {
        try
        {
            var reader = _process.StandardError;
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (line.Length == 0)
                {
                    continue;
                }

                if (TryParseProgress(line))
                {
                    continue;
                }

                LogLine?.Invoke(line);
                if (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("cannot", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warn($"ffmpeg: {line}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on stop.
        }
        catch (Exception ex)
        {
            Log.Debug($"ffmpeg stderr の読み取り終了: {ex.Message}");
        }
    }

    private bool TryParseProgress(string line)
    {
        if (line.StartsWith('['))
        {
            return false;
        }

        var eq = line.IndexOf('=');
        if (eq <= 0)
        {
            return false;
        }

        var key = line[..eq];
        var value = line[(eq + 1)..];
        switch (key)
        {
            case "frame":
                if (long.TryParse(value, out var frames)) Progress.Frames = frames;
                break;
            case "fps":
                Progress.Fps = ParseDouble(value);
                break;
            case "total_size":
                if (long.TryParse(value, out var size)) Progress.TotalBytes = size;
                break;
            case "drop_frames":
                if (long.TryParse(value, out var drop)) Progress.DropFrames = drop;
                break;
            case "dup_frames":
                if (long.TryParse(value, out var dup)) Progress.DupFrames = dup;
                break;
            case "speed":
                Progress.Speed = ParseDouble(value.TrimEnd('x'));
                break;
            case "out_time_us":
                if (long.TryParse(value, out var us)) Progress.OutTimeSeconds = us / 1_000_000.0;
                break;
            case "out_time_ms":
                // Legacy: actually microseconds in some builds. Only use when out_time_us absent.
                break;
            case "progress":
                Progress.UpdatedAt = DateTime.Now;
                return true;
            default:
                // Other "-progress" keys (bitrate, out_time, stream_0_0_q, ...) are noise here;
                // swallowing them keeps the app from writing those lines to the log every frame.
                return key.All(c => char.IsLower(c) || char.IsDigit(c) || c == '_');
        }

        Progress.UpdatedAt = DateTime.Now;
        return true;
    }

    private static double ParseDouble(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private TimeSpan SafeCpuTime()
    {
        try
        {
            return _process.HasExited ? TimeSpan.Zero : _process.TotalProcessorTime;
        }
        catch (Exception)
        {
            return TimeSpan.Zero;
        }
    }

    public void Dispose()
    {
        // Order matters: kill the process first so its stdout reaches EOF, then let the pumps
        // drain the remaining bytes (that is how the final fragment reaches the ring). Cancelling
        // first would drop whatever is still in the pipe.
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"ffmpeg の終了処理: {ex.Message}");
        }

        try
        {
            _stdoutPump?.Wait(3000);
            _stderrPump?.Wait(1000);
        }
        catch (Exception)
        {
            // Ignore.
        }

        try
        {
            _cts.Cancel();
        }
        catch (Exception)
        {
            // Ignore.
        }

        _process.Dispose();
        _cts.Dispose();
    }
}
