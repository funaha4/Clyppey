using System.Globalization;
using System.Text.RegularExpressions;

namespace Clyppey.Core.Editing;

/// <summary>
/// Keyframe positions of a finished clip.
///
/// Cuts are made with "-c copy", which can only start on a keyframe, so every cut boundary has
/// to be expressed in keyframe terms. Capture encodes with a GOP of exactly one second, so the
/// index is usually 0, 1, 2, ..., but the editor also opens clips that came from elsewhere,
/// which is why this is measured instead of assumed.
/// </summary>
public static class KeyframeIndex
{
    private static readonly Regex PtsRx = new(@"pts_time:([\d.]+)", RegexOptions.Compiled);

    public static async Task<IReadOnlyList<double>> ReadAsync(
        string ffmpegPath, string path, CancellationToken cancellationToken = default)
    {
        // "-skip_frame nokey" makes the decoder output keyframes only. The packets still have to
        // be read and the keyframes still have to be decoded, which is why a 4 minute 1080p clip
        // took 7.1 s. Hardware decoding does the same work in 2.1 s (measured, identical 252
        // entries), but it is only a hint: on a machine that cannot use it ffmpeg exits without
        // printing anything, so an empty result falls back to the software path.
        var times = await ScanAsync(ffmpegPath, path, hardware: true, cancellationToken).ConfigureAwait(false);
        if (times.Count > 0)
        {
            return times;
        }

        return await ScanAsync(ffmpegPath, path, hardware: false, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<double>> ScanAsync(
        string ffmpegPath, string path, bool hardware, CancellationToken cancellationToken)
    {
        var args = new List<string> { "-hide_banner", "-nostats", "-v", "info" };

        if (hardware)
        {
            args.Add("-hwaccel");
            args.Add("d3d11va");
        }

        args.Add("-skip_frame");
        args.Add("nokey");
        args.Add("-i");
        args.Add(path);
        args.Add("-an");
        args.Add("-vf");
        args.Add("showinfo");
        args.Add("-f");
        args.Add("null");
        args.Add("-");

        var (exitCode, _, stderr) = await FfmpegExec
            .RunAsync(ffmpegPath, args, null, cancellationToken)
            .ConfigureAwait(false);

        if (exitCode != 0 && stderr.Length == 0)
        {
            return Array.Empty<double>();
        }

        var times = new List<double>();
        foreach (Match match in PtsRx.Matches(stderr))
        {
            if (double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
            {
                times.Add(t);
            }
        }

        times.Sort();
        return times;
    }

    /// <summary>Largest keyframe at or before <paramref name="time"/>; 0 when the index is unusable.</summary>
    public static double Before(IReadOnlyList<double> keyframes, double time)
    {
        var best = 0.0;
        foreach (var k in keyframes)
        {
            if (k <= time + 1e-4)
            {
                best = k;
            }
            else
            {
                break;
            }
        }

        return best;
    }

    /// <summary>Smallest keyframe at or after <paramref name="time"/>; +infinity when none.</summary>
    public static double AtOrAfter(IReadOnlyList<double> keyframes, double time)
    {
        foreach (var k in keyframes)
        {
            if (k >= time - 1e-4)
            {
                return k;
            }
        }

        return double.PositiveInfinity;
    }
}
