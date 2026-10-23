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
            // Saving must never compete with the game for CPU or disk.
            process.PriorityClass = ProcessPriorityClass.Idle;
        }
        catch (Exception ex)
        {
            Log.Debug($"保存プロセスの優先度を下げられません: {ex.Message}");
        }

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdin = process.StandardInput.BaseStream;
        string? writeError = null;

        try
        {
            await stdin.WriteAsync(snapshot.InitSegment, cancellationToken).ConfigureAwait(false);
            foreach (var fragment in snapshot.Fragments)
            {
                if (fragment.FilePath is not null)
                {
                    using var source = new FileStream(fragment.FilePath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);
                    await source.CopyToAsync(stdin, 1 << 20, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await stdin.WriteAsync(fragment.Data, cancellationToken).ConfigureAwait(false);
                }
            }

            await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // ffmpeg exited early; the real reason will be on stderr.
            writeError = ex.Message;
        }

        try
        {
            process.StandardInput.Close();
        }
        catch (Exception)
        {
            // Already closed.
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var stderr = string.Empty;
        try
        {
            stderr = (await stderrTask.ConfigureAwait(false)).Trim();
        }
        catch (Exception)
        {
            // Ignore.
        }

        if (process.ExitCode != 0 || !File.Exists(outputPath))
        {
            var reason = stderr.Length > 0 ? stderr : (writeError ?? "原因不明のエラー");
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
