using System.Globalization;
using System.Text.RegularExpressions;

namespace Clyppey.Core.Editing;

public sealed record MediaInfo(
    bool Success,
    double Duration,
    int Width,
    int Height,
    double Fps,
    bool HasAudio,
    string VideoCodec,
    string? Error)
{
    /// <summary>False for anything we cannot cut (audio-only, truncated, unreadable).</summary>
    public bool IsValid => Success && Duration > 0.05 && Width > 0 && Height > 0;

    public double FrameSeconds => Fps > 0.1 ? 1.0 / Fps : 1.0 / 60.0;

    public static MediaInfo Fail(string error) => new(false, 0, 0, 0, 0, false, string.Empty, error);
}

/// <summary>
/// Reads clip metadata from "ffmpeg -i". ffprobe is not shipped with the app (the bundle is
/// ffmpeg only), and the -i banner carries duration, size, frame rate and audio presence.
/// The exit code of "-i" is always non-zero, so only the text matters.
/// </summary>
public static class MediaProbe
{
    private static readonly Regex DurationRx =
        new(@"Duration:\s*(\d+):(\d{2}):(\d{2}(?:\.\d+)?)", RegexOptions.Compiled);

    private static readonly Regex VideoRx =
        new(@"Stream #\d+:\d+.*?: Video: (\w+).*?(\d{2,5})x(\d{2,5})", RegexOptions.Compiled);

    private static readonly Regex FpsRx = new(@"([\d.]+)\s*fps", RegexOptions.Compiled);

    private static readonly Regex AudioRx = new(@"Stream #\d+:\d+.*?: Audio: ", RegexOptions.Compiled);

    public static async Task<MediaInfo> ReadAsync(
        string ffmpegPath, string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return MediaInfo.Fail($"ファイルがありません: {path}");
        }

        var (_, _, stderr) = await FfmpegExec
            .RunAsync(ffmpegPath, new[] { "-hide_banner", "-i", path }, null, cancellationToken)
            .ConfigureAwait(false);

        return Parse(stderr);
    }

    public static MediaInfo Parse(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return MediaInfo.Fail("ffmpeg の出力を読めませんでした");
        }

        var durationMatch = DurationRx.Match(stderr);
        if (!durationMatch.Success)
        {
            return MediaInfo.Fail(stderr.Length > 300 ? stderr[^300..] : stderr);
        }

        var duration =
            int.Parse(durationMatch.Groups[1].Value, CultureInfo.InvariantCulture) * 3600 +
            int.Parse(durationMatch.Groups[2].Value, CultureInfo.InvariantCulture) * 60 +
            double.Parse(durationMatch.Groups[3].Value, CultureInfo.InvariantCulture);

        var video = VideoRx.Match(stderr);
        var fpsMatch = FpsRx.Match(stderr);
        var fps = fpsMatch.Success
            ? double.Parse(fpsMatch.Groups[1].Value, CultureInfo.InvariantCulture)
            : 0;

        // Container-level streams are listed as "Stream #0:1" etc; the demo/attachment lines
        // are not, so a plain match on ": Audio:" is enough.
        var hasAudio = AudioRx.IsMatch(stderr);

        if (!video.Success)
        {
            return MediaInfo.Fail("映像ストリームがありません");
        }

        return new MediaInfo(
            Success: true,
            Duration: duration,
            Width: int.Parse(video.Groups[2].Value, CultureInfo.InvariantCulture),
            Height: int.Parse(video.Groups[3].Value, CultureInfo.InvariantCulture),
            Fps: fps > 0 ? fps : 60,
            HasAudio: hasAudio,
            VideoCodec: video.Groups[1].Value,
            Error: null);
    }
}
