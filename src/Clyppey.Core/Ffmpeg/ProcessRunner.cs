using System.Diagnostics;

namespace Clyppey.Core.Ffmpeg;

/// <summary>Small helper for fire-and-forget ffmpeg invocations (remuxing, thumbnails).</summary>
public static class ProcessRunner
{
    public static async Task<(bool Success, string Error)> RunAsync(
        string exe,
        IEnumerable<string> args,
        int timeoutMs = 120_000,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return (false, "プロセスを開始できませんでした");
            }

            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            _ = process.StandardOutput.ReadToEndAsync(cancellationToken);

            var completed = await Task.WhenAny(
                process.WaitForExitAsync(cancellationToken),
                Task.Delay(timeoutMs, cancellationToken)).ConfigureAwait(false);

            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                return (false, "タイムアウト");
            }

            _ = completed;

            var error = string.Empty;
            try
            {
                error = (await stderrTask.ConfigureAwait(false)).Trim();
            }
            catch (Exception)
            {
                // Ignore.
            }

            return (process.ExitCode == 0, error);
        }
        catch (OperationCanceledException)
        {
            return (false, "キャンセル");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Runs ffmpeg and returns raw stdout bytes (used for monitor thumbnails).</summary>
    public static async Task<byte[]?> RunForBytesAsync(
        string exe, IEnumerable<string> args, int timeoutMs = 20_000, CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            _ = process.StandardError.ReadToEndAsync(cancellationToken);
            using var memory = new MemoryStream();
            var stdout = process.StandardOutput.BaseStream;
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = await stdout.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                memory.Write(buffer, 0, read);
            }

            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(); } catch (Exception) { }
                Log.Debug($"ffmpeg 出力取得: タイムアウト ({exe})");
                return null;
            }

            if (process.ExitCode != 0)
            {
                Log.Debug($"ffmpeg 出力取得: exit={process.ExitCode} bytes={memory.Length} ({exe})");
                return null;
            }

            return memory.Length > 0 ? memory.ToArray() : null;
        }
        catch (Exception ex)
        {
            Log.Debug($"ffmpeg 実行に失敗: {ex.Message}");
            return null;
        }
    }
}
