using System.Globalization;
using Clyppey.Core.Ffmpeg;
using Clyppey.Core.Settings;

namespace Clyppey.Core.Capture;

/// <summary>Everything needed to describe one capture pipeline.</summary>
public sealed record CaptureOptions
{
    public required string FfmpegPath { get; init; }

    public required EncoderProfile Encoder { get; init; }

    public int OutputIndex { get; init; }

    public int Fps { get; init; } = 60;

    public RateMode Rate { get; init; } = RateMode.Cbr;

    public int BitrateMbps { get; init; } = 25;

    public int QualityLevel { get; init; } = 21;

    public bool DrawMouse { get; init; } = true;

    public static CaptureOptions Create(AppSettings settings, EncoderProfile encoder, string ffmpegPath) => new()
    {
        FfmpegPath = ffmpegPath,
        Encoder = encoder,
        OutputIndex = Math.Max(0, settings.MonitorIndex),
        Fps = settings.EffectiveFps,
        Rate = settings.Rate,
        BitrateMbps = settings.EffectiveBitrateMbps,
        QualityLevel = Math.Clamp(settings.QualityLevel, 1, 51),
        DrawMouse = settings.DrawMouse
    };
}

public static class FfmpegCommandBuilder
{
    private static string Fmt(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// Builds the live capture command.
    ///
    /// The chain is deliberately the same shape NVIDIA uses for Instant Replay:
    ///   Desktop Duplication (GPU)  ->  hardware encoder (GPU)  ->  fragmented MP4
    /// so no pixel ever touches system memory. Fragments are keyframe-aligned, which is what
    /// lets us keep encoded bytes in RAM and cut them out later with a stream copy.
    /// </summary>
    public static List<string> BuildCaptureArgs(CaptureOptions options)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-loglevel", "warning",
            "-nostats",
            "-progress", "pipe:2",

            // Input: DXGI Desktop Duplication, returns D3D11 hardware frames.
            "-f", "lavfi",
            "-i", $"ddagrab=output_idx={options.OutputIndex}:framerate={options.Fps}:draw_mouse={(options.DrawMouse ? 1 : 0)}"
        };

        args.AddRange(EncoderOptions.Build(
            options.Encoder,
            options.Fps,
            options.Rate,
            options.BitrateMbps,
            options.QualityLevel));

        // Fragmented MP4: a keyframe starts a new moof, so any fragment boundary is a valid
        // clipping point. empty_moov means the header is complete before the first frame.
        args.AddRange(new[]
        {
            "-f", "mp4",
            "-movflags", "+frag_keyframe+empty_moov+default_base_moof",
            "pipe:1"
        });

        return args;
    }

    /// <summary>Audio mix description: which WAVs exist and how loud each should be.</summary>
    public sealed record AudioMix(string? DesktopWavPath, string? MicrophoneWavPath, double MicrophoneVolume)
    {
        public bool HasAny => !string.IsNullOrWhiteSpace(DesktopWavPath) ||
                              !string.IsNullOrWhiteSpace(MicrophoneWavPath);
    }

    /// <summary>
    /// Command that turns buffered fragments back into a normal, seekable MP4.
    /// Video is stream-copied (no re-encode, no quality loss). Audio is mixed from the captured
    /// desktop and microphone tracks, then encoded to AAC - a WAV would make clips enormous.
    /// </summary>
    public static List<string> BuildRemuxArgs(
        string outputPath, AudioMix? audio = null, int audioDelayMs = 0, bool withFastStart = true,
        double durationSeconds = 0)
        => BuildRemux(new[] { "-f", "mov", "-i", "pipe:0" }, outputPath, audio, audioDelayMs,
            withFastStart, durationSeconds);

    /// <summary>Same as <see cref="BuildRemuxArgs"/> but for an already complete MP4 on disk.</summary>
    public static List<string> BuildFileRemuxArgs(
        string inputPath, string outputPath, AudioMix? audio = null, int audioDelayMs = 0,
        bool withFastStart = true, double durationSeconds = 0)
        => BuildRemux(new[] { "-i", inputPath }, outputPath, audio, audioDelayMs,
            withFastStart, durationSeconds);

    private static List<string> BuildRemux(
        IEnumerable<string> inputArgs,
        string outputPath,
        AudioMix? audio,
        int audioDelayMs,
        bool withFastStart,
        double durationSeconds)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-loglevel", "error",
            "-y"
        };

        args.AddRange(inputArgs);

        var sources = new List<(string Path, double Volume)>();
        if (audio is not null)
        {
            if (!string.IsNullOrWhiteSpace(audio.DesktopWavPath))
            {
                sources.Add((audio.DesktopWavPath!, 1.0));
            }

            if (!string.IsNullOrWhiteSpace(audio.MicrophoneWavPath))
            {
                // 0..2 from the UI; clamp so a mistake cannot blow out the mix.
                sources.Add((audio.MicrophoneWavPath!, Math.Clamp(audio.MicrophoneVolume, 0.0, 2.0)));
            }
        }

        foreach (var (path, _) in sources)
        {
            if (audioDelayMs != 0)
            {
                args.Add("-itsoffset");
                args.Add(Fmt(audioDelayMs / 1000.0));
            }

            args.Add("-i");
            args.Add(path);
        }

        args.AddRange(new[] { "-c:v", "copy" });

        if (sources.Count > 0)
        {
            // Normalise every source to 48 kHz stereo float first: the microphone may be mono
            // and a different sample rate to the loopback stream, and amix requires matching
            // formats. A limiter keeps a loud mic plus loud game audio from clipping.
            var chains = new List<string>();
            for (var i = 0; i < sources.Count; i++)
            {
                var inputIndex = i + 1;
                chains.Add($"[{inputIndex}:a]aresample=48000," +
                           $"aformat=sample_fmts=fltp:channel_layouts=stereo," +
                           $"volume={Fmt(sources[i].Volume)}[s{i}]");
            }

            if (sources.Count == 1)
            {
                chains.Add("[s0]anull[mixed]");
            }
            else
            {
                chains.Add("[s0][s1]amix=inputs=2:duration=longest:dropout_transition=0[mixed]");
            }

            chains.Add("[mixed]alimiter=limit=0.95[out]");

            args.AddRange(new[] { "-filter_complex", string.Join(";", chains) });
            args.AddRange(new[] { "-map", "0:v:0", "-map", "[out]" });
            args.AddRange(new[] { "-c:a", "aac", "-b:a", "160k", "-ar", "48000" });

            // The video defines the clip length. "-t" is used instead of "-shortest" because
            // ffmpeg 7.x can refuse to terminate when shortest meets an endless audio filter,
            // and a short audio track must never chop the end off the video.
            if (durationSeconds > 0)
            {
                args.Add("-t");
                args.Add(Fmt(durationSeconds));
            }
        }

        args.AddRange(new[] { "-avoid_negative_ts", "make_zero" });

        if (withFastStart)
        {
            args.AddRange(new[] { "-movflags", "+faststart" });
        }

        args.Add(outputPath);
        return args;
    }

    /// <summary>One-frame grab used to identify which DXGI output maps to which monitor.</summary>
    public static List<string> BuildSnapshotArgs(int outputIndex)
    {
        return new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-f", "lavfi",
            "-i", $"ddagrab=output_idx={outputIndex}:framerate=1:draw_mouse=1",
            "-frames:v", "1",
            // ddagrab hands out D3D11 frames; a software JPEG needs them downloaded, then
            // scaled down so the picker thumbnail stays small.
            "-vf", "hwdownload,format=bgra,scale=480:-2",
            "-c:v", "mjpeg",
            "-q:v", "4",
            "-f", "image2pipe",
            "pipe:1"
        };
    }
}
