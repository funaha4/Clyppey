namespace Clyppey.Core.Ffmpeg;

/// <summary>
/// Builds the encoder-specific arguments. Kept in one place so the capability probe and the
/// real capture pipeline always test/drive identical settings.
/// </summary>
public static class EncoderOptions
{
    /// <summary>
    /// GOP length: one keyframe per second. Every keyframe is also a fragment boundary in the
    /// MP4 output, which is what makes "save the last N seconds" possible without re-encoding.
    /// </summary>
    public static int Gop(int fps) => Math.Max(1, fps);

    public static int BufSizeKbps(int bitrateMbps) => bitrateMbps * 2 * 1000;

    /// <summary>Full production argument block for a capture session.</summary>
    public static List<string> Build(
        EncoderProfile profile, int fps, RateMode rate, int bitrateMbps, int quality, bool hardwareInput = true)
    {
        var args = new List<string> { "-c:v", profile.Name };
        var gop = Gop(fps);
        var kbps = Math.Max(500, bitrateMbps * 1000);

        // Desktop Duplication hands us D3D11 frames. Hardware encoders accept them directly
        // (zero copies). A software encoder needs them downloaded first.
        //
        // Never pass "-pix_fmt" on the hardware paths: it pins the encoder input format and
        // makes ffmpeg insert a software conversion, which silently breaks the zero-copy chain.
        if (!profile.Hardware && hardwareInput)
        {
            args.AddRange(new[] { "-vf", "hwdownload,format=bgra" });
        }

        switch (profile.Family)
        {
            case EncoderFamily.Nvenc:
                // p1 = fastest, p7 = best quality. "ull" = ultra low latency (no frame reordering).
                args.AddRange(new[]
                {
                    "-preset", "p4",
                    "-tune", "ull",
                    "-g", gop.ToString(),
                    "-bf", "0",
                    "-rc-lookahead", "0",
                    "-delay", "0",
                    "-surfaces", "4"
                });

                if (profile.Codec == CodecKind.H264)
                {
                    args.AddRange(new[] { "-profile:v", "high" });
                }
                else if (profile.Codec == CodecKind.Hevc)
                {
                    args.AddRange(new[] { "-profile:v", "main" });
                }

                switch (rate)
                {
                    case RateMode.Cbr:
                        args.AddRange(new[]
                        {
                            "-rc", "cbr",
                            "-b:v", $"{kbps}k",
                            "-maxrate", $"{kbps}k",
                            "-bufsize", $"{BufSizeKbps(bitrateMbps)}k"
                        });
                        break;
                    case RateMode.Vbr:
                        args.AddRange(new[]
                        {
                            "-rc", "vbr",
                            "-b:v", $"{kbps}k",
                            "-maxrate", $"{kbps * 2}k",
                            "-bufsize", $"{BufSizeKbps(bitrateMbps)}k"
                        });
                        break;
                    default:
                        args.AddRange(new[] { "-rc", "vbr", "-cq", quality.ToString(), "-b:v", "0" });
                        break;
                }
                break;

            case EncoderFamily.Amf:
                args.AddRange(new[]
                {
                    "-usage", "ultralowlatency",
                    "-quality", "balanced",
                    "-g", gop.ToString()
                });
                switch (rate)
                {
                    case RateMode.Cbr:
                        args.AddRange(new[]
                        {
                            "-rc", "cbr",
                            "-b:v", $"{kbps}k",
                            "-maxrate", $"{kbps}k",
                            "-bufsize", $"{BufSizeKbps(bitrateMbps)}k"
                        });
                        break;
                    case RateMode.Vbr:
                        args.AddRange(new[]
                        {
                            "-rc", "vbr_peak",
                            "-b:v", $"{kbps}k",
                            "-maxrate", $"{kbps * 2}k"
                        });
                        break;
                    default:
                        args.AddRange(new[] { "-rc", "qvbr", "-qvbr_quality_level", quality.ToString(), "-b:v", "0" });
                        break;
                }
                break;

            case EncoderFamily.Qsv:
                args.AddRange(new[]
                {
                    "-preset", "veryfast",
                    "-look_ahead", "0",
                    "-g", gop.ToString()
                });
                if (rate == RateMode.Cq)
                {
                    args.AddRange(new[] { "-global_quality", quality.ToString() });
                }
                else
                {
                    args.AddRange(new[] { "-b:v", $"{kbps}k", "-maxrate", $"{kbps}k" });
                }
                break;

            default: // software
                args.AddRange(new[]
                {
                    "-preset", "ultrafast",
                    "-tune", "zerolatency",
                    "-g", gop.ToString(),
                    "-pix_fmt", "yuv420p"
                });
                if (rate == RateMode.Cq)
                {
                    args.AddRange(new[] { "-crf", quality.ToString() });
                }
                else
                {
                    args.AddRange(new[]
                    {
                        "-b:v", $"{kbps}k",
                        "-maxrate", $"{kbps}k",
                        "-bufsize", $"{BufSizeKbps(bitrateMbps)}k"
                    });
                }
                break;
        }

        // Container tags: hvc1 keeps HEVC playing in QuickTime/Windows/browsers.
        if (profile.Codec == CodecKind.Hevc)
        {
            args.AddRange(new[] { "-tag:v", "hvc1" });
        }

        return args;
    }

    /// <summary>Arguments used by the capability probe (tiny, fast, throwaway).</summary>
    public static List<string> BuildProbeArgs(EncoderProfile profile, bool useCapture)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error"
        };

        if (useCapture)
        {
            args.AddRange(new[] { "-f", "lavfi", "-i", "ddagrab=framerate=30:video_size=320x180:draw_mouse=0" });
        }
        else
        {
            args.AddRange(new[] { "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30" });
        }

        args.AddRange(new[] { "-frames:v", "6" });

        // The capture probe must use the exact hardware-input shape the real pipeline uses,
        // otherwise it would not prove the zero-copy path works.
        args.AddRange(Build(profile, fps: 30, RateMode.Cbr, bitrateMbps: 4, quality: 23,
            hardwareInput: useCapture));
        args.AddRange(new[] { "-f", "null", "-" });
        return args;
    }
}
