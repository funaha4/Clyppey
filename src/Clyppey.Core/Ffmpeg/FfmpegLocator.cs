using System.Diagnostics;

namespace Clyppey.Core.Ffmpeg;

/// <summary>A concrete encoder we can drive, e.g. h264_nvenc.</summary>
public sealed record EncoderProfile(CodecKind Codec, string Name, EncoderFamily Family, bool Hardware)
{
    public string DisplayName => Name switch
    {
        "h264_nvenc" => "NVENC H.264",
        "hevc_nvenc" => "NVENC HEVC",
        "av1_nvenc" => "NVENC AV1",
        "h264_amf" => "AMF H.264",
        "hevc_amf" => "AMF HEVC",
        "av1_amf" => "AMF AV1",
        "h264_qsv" => "QSV H.264",
        "hevc_qsv" => "QSV HEVC",
        "libx264" => "x264 (CPU)",
        "libx265" => "x265 (CPU)",
        _ => Name
    };
}

/// <summary>Result of probing one encoder on this machine.</summary>
public sealed record ProbedEncoder(
    string Name,
    EncoderFamily Family,
    CodecKind Codec,
    bool Hardware,
    bool PlainWorks,
    bool ZeroCopyWorks,
    string? Error);

public sealed record ProbeResults
{
    public string FfmpegPath { get; init; } = string.Empty;

    public string FfmpegVersion { get; init; } = string.Empty;

    /// <summary>Fingerprint of the ffmpeg binary the results belong to.</summary>
    public string FfmpegStamp { get; init; } = string.Empty;

    public DateTimeOffset ProbedAt { get; init; }

    public List<ProbedEncoder> Encoders { get; init; } = new();

    /// <summary>Set when Desktop Duplication itself could not be opened.</summary>
    public string? CaptureError { get; init; }

    public bool AnyZeroCopy => Encoders.Any(e => e.ZeroCopyWorks);

    public IEnumerable<ProbedEncoder> For(CodecKind codec) =>
        Encoders.Where(e => e.Codec == codec && (e.PlainWorks || e.ZeroCopyWorks));
}

/// <summary>
/// Works out what this machine can actually do. Everything is verified by really running
/// ffmpeg, because driver/encoder support cannot be inferred from version strings alone.
/// </summary>
public static class EncoderProbe
{
    /// <summary>Probe order defines the preference order.</summary>
    public static readonly IReadOnlyList<EncoderProfile> Candidates = new[]
    {
        new EncoderProfile(CodecKind.H264, "h264_nvenc", EncoderFamily.Nvenc, true),
        new EncoderProfile(CodecKind.Hevc, "hevc_nvenc", EncoderFamily.Nvenc, true),
        new EncoderProfile(CodecKind.Av1, "av1_nvenc", EncoderFamily.Nvenc, true),
        new EncoderProfile(CodecKind.H264, "h264_amf", EncoderFamily.Amf, true),
        new EncoderProfile(CodecKind.Hevc, "hevc_amf", EncoderFamily.Amf, true),
        new EncoderProfile(CodecKind.Av1, "av1_amf", EncoderFamily.Amf, true),
        new EncoderProfile(CodecKind.H264, "h264_qsv", EncoderFamily.Qsv, true),
        new EncoderProfile(CodecKind.Hevc, "hevc_qsv", EncoderFamily.Qsv, true),
        new EncoderProfile(CodecKind.H264, "libx264", EncoderFamily.Software, false),
        new EncoderProfile(CodecKind.Hevc, "libx265", EncoderFamily.Software, false)
    };

    public static ProbeResults Probe(string ffmpegPath, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var version = FfmpegLocator.GetVersion(ffmpegPath) ?? "unknown";
        var stamp = BuildStamp(ffmpegPath);
        var results = new List<ProbedEncoder>();
        string? captureError = null;

        foreach (var profile in Candidates)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"{profile.DisplayName} を確認中...");

            // 1) Can the encoder open at all? (software frames, no capture involvement)
            var plain = Run(ffmpegPath, EncoderOptions.BuildProbeArgs(profile, useCapture: false), 20000, ct);
            if (!plain.Success)
            {
                results.Add(new ProbedEncoder(profile.Name, profile.Family, profile.Codec, profile.Hardware,
                    PlainWorks: false, ZeroCopyWorks: false, Error: Trim(plain.Error)));
                continue;
            }

            // 2) Can it take D3D11 frames straight from Desktop Duplication (the zero-copy path)?
            var zero = Run(ffmpegPath, EncoderOptions.BuildProbeArgs(profile, useCapture: true), 20000, ct);
            if (!zero.Success && captureError is null && LooksLikeCaptureFailure(zero.Error))
            {
                captureError = Trim(zero.Error);
            }

            results.Add(new ProbedEncoder(profile.Name, profile.Family, profile.Codec, profile.Hardware,
                PlainWorks: true,
                // Only a hardware encoder can consume D3D11 frames without a download.
                ZeroCopyWorks: zero.Success && profile.Hardware,
                Error: zero.Success ? null : Trim(zero.Error)));
        }

        progress?.Report("完了");
        return new ProbeResults
        {
            FfmpegPath = ffmpegPath,
            FfmpegVersion = version,
            FfmpegStamp = stamp,
            ProbedAt = DateTimeOffset.Now,
            Encoders = results,
            CaptureError = captureError
        };
    }

    public static string BuildStamp(string ffmpegPath)
    {
        // The probe version is part of the stamp so that changing probe/pipeline logic
        // invalidates old cached capability results.
        const int ProbeVersion = 2;
        try
        {
            var info = new FileInfo(ffmpegPath);
            return $"{ffmpegPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|v{ProbeVersion}";
        }
        catch (Exception)
        {
            return $"{ffmpegPath}|v{ProbeVersion}";
        }
    }

    private static bool LooksLikeCaptureFailure(string error) =>
        error.Contains("ddagrab", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("Desktop Duplication", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("dxgi", StringComparison.OrdinalIgnoreCase);

    private static string Trim(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToArray();
        var joined = string.Join(" / ", lines);
        return joined.Length <= 300 ? joined : joined[..300];
    }

    internal static (bool Success, string Error) Run(
        string exe, IEnumerable<string> args, int timeoutMs, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return (false, "プロセスを開始できませんでした");
            }

            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);

            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
                return (false, "タイムアウト");
            }

            proc.WaitForExit();
            var err = SafeResult(stderrTask);
            _ = stdoutTask;
            return (proc.ExitCode == 0, err);
        }
        catch (OperationCanceledException)
        {
            return (false, "キャンセル");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string SafeResult(Task<string> task)
    {
        try
        {
            return task.Wait(TimeSpan.FromSeconds(5)) ? task.Result : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}

public static class FfmpegLocator
{
    public static string? Find() => AppPaths.FindFfmpeg();

    public static string? GetVersion(string ffmpegPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-version");

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return null;
            }

            var text = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10000);
            return text.Split('\n').FirstOrDefault()?.Trim();
        }
        catch (Exception ex)
        {
            Log.Warn($"ffmpeg のバージョン取得に失敗: {ex.Message}");
            return null;
        }
    }
}
