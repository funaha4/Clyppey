using System.Diagnostics;
using Clyppey.Core.Capture;

namespace Clyppey.Core.Mp4;

public sealed record ClipWriteResult(bool Success, string OutputPath, long Bytes, double Duration, string? Error);

/// <summary>
/// Writes a clip by streaming the buffered fragments into a remuxing ffmpeg process.
/// The video bitstream is copied, never re-encoded, so a 60 second save takes well under a
/// second and the result is bit-identical to what was captured.
/// </summary>
public static class ClipWriter
{
    public static async Task<ClipWriteResult> WriteAsync(
        string ffmpegPath,
        RingSnapshot snapshot,
        string outputPath,
        FfmpegCommandBuilder.AudioMix? audio = null,
        int audioDelayMs = 0,
        CancellationToken cancellationToken = default)
    {
        if (snapshot.InitSegment is null || snapshot.IsEmpty)
        {
            return new ClipWriteResult(false, outputPath, 0, 0, "バッファに映像がありません");
        }

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // faststart rewrites the whole file to move the index to the front. For a multi-hundred
        // MB clip that doubled the disk traffic and made games hitch, so it is off: a plain MP4
        // with the index at the end opens fine locally.
        var args = FfmpegCommandBuilder.BuildRemuxArgs(
            outputPath, audio, audioDelayMs, withFastStart: false, durationSeconds: snapshot.Duration);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            return new ClipWriteResult(false, outputPath, 0, 0, "ffmpeg を起動できませんでした");
        }

        try
        {
            // Saving must never compete with the game for CPU or disk (AGENTS.md §2 #5).
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex)
        {
            Log.Debug($"保存プロセスの優先度を下げられません: {ex.Message}");
        }

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdin = process.StandardInput.BaseStream;
        string? writeError = null;

        // A hung disk or ffmpeg must not hold _busy forever: cap the whole save.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);
        var linkedToken = linkedCts.Token;

        try
        {
            await stdin.WriteAsync(snapshot.InitSegment, linkedToken).ConfigureAwait(false);
            foreach (var fragment in snapshot.Fragments)
            {
                if (fragment.FilePath is not null)
                {
                    using var source = new FileStream(fragment.FilePath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);
                    await source.CopyToAsync(stdin, 1 << 20, linkedToken).ConfigureAwait(false);
                }
                else
                {
                    await stdin.WriteAsync(fragment.Data, linkedToken).ConfigureAwait(false);
                }
            }

            await stdin.FlushAsync(linkedToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // ffmpeg exited early / timed out; the real reason will be on stderr.
            writeError = timeoutCts.IsCancellationRequested ? "保存がタイムアウトしました (30秒)" : ex.Message;
        }

        try
        {
            process.StandardInput.Close();
        }
        catch (Exception)
        {
            // Already closed.
        }

        try
        {
            await process.WaitForExitAsync(linkedToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
            writeError ??= "保存がタイムアウトしました (30秒)";
        }

        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
            writeError ??= "保存がタイムアウトしました (30秒)";
        }

        var stderr = string.Empty;
        try
        {
            stderr = (await stderrTask.ConfigureAwait(false)).Trim();
        }
        catch (Exception)
        {
            // Ignore.
        }

        if (process.ExitCode != 0 || !File.Exists(outputPath) || writeError is not null)
        {
            var reason = writeError ?? (stderr.Length > 0 ? stderr : "原因不明のエラー");
            // Never leave a half-written clip behind: the next save would pick a _1 name
            // and the broken file would look like a real result.
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { /* best effort */ }
            return new ClipWriteResult(false, outputPath, 0, 0, Truncate(reason));
        }

        var info = new FileInfo(outputPath);
        return new ClipWriteResult(true, outputPath, info.Length, snapshot.Duration, null);
    }

    private static string Truncate(string text)
    {
        var oneLine = string.Join(" / ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0));
        return oneLine.Length <= 400 ? oneLine : oneLine[..400];
    }
}
