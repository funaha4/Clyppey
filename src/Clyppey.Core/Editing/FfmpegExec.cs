using System.Diagnostics;
using System.Text;

namespace Clyppey.Core.Editing;

/// <summary>
/// Small ffmpeg runner for the editing path. Unlike <see cref="Ffmpeg.ProcessRunner"/> it
/// streams stdout line by line, because that is where "-progress" writes, and it keeps the
/// tail of stderr so a failed cut can report the real reason instead of "exit 1".
/// </summary>
internal static class FfmpegExec
{
    private const int MaxCaptureChars = 16_000;

    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string ffmpegPath,
        IReadOnlyList<string> args,
        Action<string>? onStdOutLine = null,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            return (-1, string.Empty, "ffmpeg を起動できませんでした");
        }

        // Same promise as the capture path: never compete with a game for CPU scheduling.
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception)
        {
            // Priority is a hint; some sandboxes refuse it.
        }

        var stderr = new StringBuilder();
        var stdout = new StringBuilder();

        var errTask = ReadLinesAsync(process.StandardError, cancellationToken, line =>
        {
            if (stderr.Length < MaxCaptureChars)
            {
                stderr.AppendLine(line);
            }
        });
        var outTask = ReadLinesAsync(process.StandardOutput, cancellationToken, line =>
        {
            if (stdout.Length < 4_096)
            {
                stdout.AppendLine(line);
            }

            onStdOutLine?.Invoke(line);
        });

        try
        {
            await Task.WhenAll(errTask, outTask).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Already gone.
            }

            throw;
        }

        return (process.ExitCode, stdout.ToString(), stderr.ToString().Trim());
    }

    private static async Task ReadLinesAsync(
        StreamReader reader, CancellationToken cancellationToken, Action<string> onLine)
    {
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (line is null)
            {
                return;
            }

            onLine(line);
        }
    }
}
