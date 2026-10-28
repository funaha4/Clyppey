using System.Globalization;

namespace Clyppey.Core.Editing;

public sealed record TrimOutcome(
    bool Success,
    string OutputPath,
    double DurationSeconds,
    double RequestedSeconds,
    long Frames,
    long ExpectedFrames,
    int Junctions,
    IReadOnlyList<string> Warnings,
    string? Error)
{
    /// <summary>Frames divided by container duration; reads slightly under 60 per join.</summary>
    public double AverageFps => DurationSeconds > 0.01 ? Frames / DurationSeconds : 0;

    public static TrimOutcome Fail(string error) =>
        new(false, string.Empty, 0, 0, 0, 0, 0, Array.Empty<string>(), error);
}

/// <summary>
/// Runs a <see cref="TrimPlan"/>: segment files first, then a stream-copy concat, then a
/// verification pass. Nothing here touches the capture pipeline, so the "never re-encode"
/// rule for saving a replay still holds; this is a separate, file-to-file path.
/// </summary>
public static class TrimJob
{
    public static async Task<TrimOutcome> RunAsync(
        string ffmpegPath,
        TrimContext context,
        IProgress<double>? progress = null,
        bool verify = true,
        CancellationToken cancellationToken = default)
    {
        var plan = TrimPlanBuilder.Build(context);
        if (plan.Steps.Count == 0)
        {
            return TrimOutcome.Fail("残す区間がありません（削除区間が全体を覆っています）");
        }

        Directory.CreateDirectory(context.TempDirectory);
        try
        {
            var lines = new List<string>();
            foreach (var step in plan.Steps)
            {
                lines.Add($"file '{ToPosix(step.OutputPath)}'");
            }

            await File.WriteAllTextAsync(plan.ConcatListPath, string.Join('\n', lines) + "\n", cancellationToken)
                .ConfigureAwait(false);

            var totalWeight = 0.0;
            foreach (var step in plan.Steps)
            {
                totalWeight += step.WeightSeconds;
            }

            totalWeight = Math.Max(totalWeight, 0.1);

            var completed = 0.0;
            foreach (var step in plan.Steps)
            {
                var stepWeight = Math.Max(step.WeightSeconds, 0.01);
                var (exitCode, _, stderr) = await FfmpegExec.RunAsync(
                    ffmpegPath,
                    step.Arguments,
                    line =>
                    {
                        var t = ParseProgressSeconds(line);
                        if (t is null)
                        {
                            return;
                        }

                        var fraction = Math.Clamp(t.Value / stepWeight, 0, 1);
                        progress?.Report(Math.Clamp((completed + (fraction * stepWeight)) / totalWeight, 0, 0.95));
                    },
                    cancellationToken).ConfigureAwait(false);

                if (exitCode != 0)
                {
                    return TrimOutcome.Fail(stderr.Length > 0 ? FirstLines(stderr, 3) : $"ffmpeg が終了コード {exitCode} で止まりました");
                }

                completed += stepWeight;
            }

            var concatArgs = new List<string>
            {
                "-y", "-hide_banner", "-nostats", "-v", "error",
                "-f", "concat", "-safe", "0", "-i", plan.ConcatListPath,
                "-map", "0:v:0"
            };

            if (context.HasAudio)
            {
                concatArgs.AddRange(new[] { "-map", "0:a:0" });
            }

            concatArgs.AddRange(new[] { "-c", "copy", "-movflags", "+faststart", context.OutputPath });

            var (concatCode, _, concatError) = await FfmpegExec
                .RunAsync(ffmpegPath, concatArgs, null, cancellationToken)
                .ConfigureAwait(false);

            if (concatCode != 0 || !File.Exists(context.OutputPath))
            {
                return TrimOutcome.Fail(concatError.Length > 0 ? FirstLines(concatError, 3) : "結合に失敗しました");
            }

            progress?.Report(0.97);

            var warnings = new List<string>();
            if (plan.Warning is not null)
            {
                warnings.Add(plan.Warning);
            }

            var info = await MediaProbe.ReadAsync(ffmpegPath, context.OutputPath, cancellationToken).ConfigureAwait(false);
            if (!info.IsValid)
            {
                warnings.Add("出力ファイルを読み直せませんでした");
            }

            // Counting frames is the honest check. The container timeline drifts by about one
            // frame per join (each segment is written with its own zero-based timestamps), so
            // the average frame rate reads slightly under 60 even though every frame is still
            // 1/60 s apart and nothing was dropped.
            var expectedFrames = (long)Math.Round(plan.ExpectedSeconds * context.Fps);
            var frames = 0L;

            if (verify)
            {
                var (verifyCode, _, verifyError) = await FfmpegExec.RunAsync(
                    ffmpegPath,
                    new[]
                    {
                        "-hide_banner", "-nostats", "-progress", "pipe:1", "-v", "error",
                        "-i", context.OutputPath, "-map", "0:v:0", "-f", "null", "-"
                    },
                    line =>
                    {
                        var parsed = ParseProgressLong(line, "frame=");
                        if (parsed is not null)
                        {
                            frames = parsed.Value;
                        }
                    },
                    cancellationToken).ConfigureAwait(false);

                if (verifyCode != 0 || verifyError.Length > 0)
                {
                    warnings.Add("デコード警告: " + FirstLines(verifyError, 2));
                }
            }

            var junctions = Math.Max(0, plan.Steps.Count - 1);
            if (verify && frames > 0 && Math.Abs(frames - expectedFrames) > junctions + 3)
            {
                warnings.Add($"フレーム数が合いません: {frames} フレーム（期待 {expectedFrames}）");
            }

            progress?.Report(1.0);
            Log.Info($"クリップを書き出しました: {context.OutputPath} ({info.Duration:F2} 秒)");

            return new TrimOutcome(
                true,
                context.OutputPath,
                info.Duration,
                plan.ExpectedSeconds,
                frames,
                expectedFrames,
                junctions,
                warnings,
                null);
        }
        catch (OperationCanceledException)
        {
            return TrimOutcome.Fail("キャンセルしました");
        }
        catch (Exception ex)
        {
            return TrimOutcome.Fail(ex.Message);
        }
        finally
        {
            TryDeleteDirectory(context.TempDirectory);
        }
    }

    private static double? ParseProgressSeconds(string line)
    {
        // ffmpeg reports out_time_us (microseconds). out_time_ms carries the same value in
        // practice, so both are accepted.
        const string Us = "out_time_us=";
        const string Ms = "out_time_ms=";

        if (line.StartsWith(Us, StringComparison.Ordinal) || line.StartsWith(Ms, StringComparison.Ordinal))
        {
            var raw = line[(line.IndexOf('=') + 1)..].Trim();
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros) && micros > 0)
            {
                return micros / 1_000_000.0;
            }
        }

        return null;
    }

    private static long? ParseProgressLong(string line, string key)
    {
        if (!line.StartsWith(key, StringComparison.Ordinal))
        {
            return null;
        }

        var raw = line[key.Length..].Trim();
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string ToPosix(string path) => path.Replace('\\', '/').Replace("'", "\\'");

    private static string FirstLines(string text, int count)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var take = Math.Min(count, lines.Length);
        return string.Join(" / ", lines.Take(take).Select(l => l.Trim()));
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"作業フォルダを削除できません: {ex.Message}");
        }
    }
}
