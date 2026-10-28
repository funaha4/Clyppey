namespace Clyppey.Core.Editing;

public enum CutMode
{
    /// <summary>Frame accurate: re-encodes at most one keyframe interval per cut.</summary>
    Exact,

    /// <summary>No re-encoding at all; boundaries snap back to the previous keyframe.</summary>
    Fast
}

public sealed record TrimContext(
    string Source,
    string OutputPath,
    string TempDirectory,
    IReadOnlyList<TimeRange> Keeps,
    IReadOnlyList<double> Keyframes,
    CutMode Mode,
    double Fps,
    bool HasAudio,
    int QualityLevel = 18);

public sealed record TrimStep(string OutputPath, IReadOnlyList<string> Arguments, double WeightSeconds);

public sealed record TrimPlan(
    IReadOnlyList<TrimStep> Steps,
    string ConcatListPath,
    double ExpectedSeconds,
    string? Warning);

/// <summary>
/// Turns "keep these ranges" into concrete ffmpeg invocations.
///
/// The whole design leans on one fact: a cut with "-c copy" can only start on a keyframe.
/// Capture uses a GOP of exactly one second, so the cheap mode loses up to a second at each
/// boundary, while the exact mode re-encodes only the slice between the requested in-point and
/// the next keyframe (always under a second) and copies the rest untouched. That keeps the
/// output bit-identical to the capture except for at most one second per cut.
/// </summary>
public static class TrimPlanBuilder
{
    private static readonly string[] Common =
    {
        "-y", "-hide_banner", "-nostats", "-progress", "pipe:1", "-v", "error"
    };

    public static TrimPlan Build(TrimContext context)
    {
        var steps = new List<TrimStep>();
        var index = 0;
        var hasIndex = context.Keyframes.Count > 0;
        string? warning = null;

        foreach (var keep in context.Keeps)
        {
            if (keep.Duration < 0.05)
            {
                continue;
            }

            index++;
            var output = Path.Combine(context.TempDirectory, $"seg{index:000}.mp4");

            if (context.Mode == CutMode.Fast || !hasIndex)
            {
                var start = hasIndex
                    ? KeyframeIndex.Before(context.Keyframes, keep.Start)
                    : keep.Start;

                if (start > keep.Start)
                {
                    start = keep.Start;
                }

                steps.Add(new TrimStep(output, CopyArgs(context, start, keep.End - start, output), keep.End - start));
                continue;
            }

            var snapped = KeyframeIndex.Before(context.Keyframes, keep.Start);
            var next = KeyframeIndex.AtOrAfter(context.Keyframes, keep.Start);

            if (Math.Abs(keep.Start - snapped) < 0.002)
            {
                // Already a keyframe: nothing to re-encode.
                steps.Add(new TrimStep(output, CopyArgs(context, keep.Start, keep.Duration, output), keep.Duration));
                continue;
            }

            var nextUsable = next > keep.Start + 0.002 && next < keep.End - 0.002;
            if (!nextUsable)
            {
                // No keyframe inside this range, so the cheap path cannot represent it.
                steps.Add(new TrimStep(
                    output,
                    EncodeArgs(context, keep.Start, keep.Duration, output),
                    keep.Duration));
                warning ??= "キーコードが区間内に無いため一部を再エンコードしました";
                continue;
            }

            var headOutput = output;
            steps.Add(new TrimStep(
                headOutput,
                EncodeArgs(context, keep.Start, next - keep.Start, headOutput),
                next - keep.Start));

            index++;
            var tailOutput = Path.Combine(context.TempDirectory, $"seg{index:000}.mp4");
            steps.Add(new TrimStep(
                tailOutput,
                CopyArgs(context, next, keep.End - next, tailOutput),
                keep.End - next));
        }

        // What the plan will actually produce, which for the fast mode is longer than the
        // requested ranges because every cut start snaps back to the previous keyframe.
        var expected = 0.0;
        foreach (var step in steps)
        {
            expected += step.WeightSeconds;
        }

        return new TrimPlan(
            steps,
            Path.Combine(context.TempDirectory, "concat.txt"),
            expected,
            warning);
    }

    private static List<string> Base(string source, double start)
    {
        // "-ss" before "-i" is the fast seek; "-t" must stay an OUTPUT option (after "-i").
        // Given before the input it limits how much of the input is read, measured on the
        // input timeline, which silently produced segments twice as long as requested.
        var args = new List<string>(Common)
        {
            "-ss", Seconds(start),
            "-i", source
        };
        return args;
    }

    private static List<string> CopyArgs(TrimContext context, double start, double length, string output)
    {
        var args = Base(context.Source, start);
        args.AddRange(new[] { "-t", Seconds(length), "-map", "0:v:0" });
        if (context.HasAudio)
        {
            // Audio is copied, not re-encoded: an AAC encoder adds ~21 ms of priming padding to
            // every segment, and four of those pushed the joined duration (and the reported
            // frame rate) noticeably away from the source. Every segment comes from the same
            // file, so the AAC headers already agree and the concat step stays happy.
            args.AddRange(new[] { "-map", "0:a:0", "-c:v", "copy", "-c:a", "copy" });
        }
        else
        {
            args.AddRange(new[] { "-c:v", "copy" });
        }

        args.AddRange(new[] { "-avoid_negative_ts", "make_zero", output });
        return args;
    }

    private static List<string> EncodeArgs(TrimContext context, double start, double length, string output)
    {
        var args = Base(context.Source, start);
        args.AddRange(new[] { "-t", Seconds(length), "-map", "0:v:0" });
        if (context.HasAudio)
        {
            args.AddRange(new[] { "-map", "0:a:0" });
        }

        args.AddRange(new[]
        {
            "-c:v", "h264_nvenc",
            // Same shape as the capture encoder (no B-frames, one second GOP) so the segment
            // matches the copied tail closely enough to concatenate without re-timing.
            "-preset", "p4",
            "-tune", "ull",
            "-g", Math.Max(1, (int)Math.Round(context.Fps)).ToString(),
            "-bf", "0",
            "-profile:v", "high",
            "-rc", "vbr",
            "-cq", context.QualityLevel.ToString(),
            "-b:v", "0",
            "-pix_fmt", "yuv420p",
            "-fps_mode", "passthrough"
        });

        if (context.HasAudio)
        {
            // Audio must be re-encoded here, not copied: after an input seek the audio demuxer
            // hands over whole packets from before the cut point, which turned a 0.2 s head
            // into a 1.0 s segment (measured). Re-encoding costs ~20 ms of encoder padding.
            args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2" });
        }

        args.AddRange(new[] { "-avoid_negative_ts", "make_zero", output });
        return args;
    }

    private static string Seconds(double value) =>
        value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
