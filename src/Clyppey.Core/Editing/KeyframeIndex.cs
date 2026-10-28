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
        // "-skip_frame nokey" makes the decoder output keyframes only, so this decodes a
        // couple of frames per second of video rather than all of them.
        var args = new[]
        {
            "-hide_banner", "-nostats", "-v", "info",
            "-skip_frame", "nokey",
            "-i", path,
            "-an", "-vf", "showinfo", "-f", "null", "-"
        };

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
