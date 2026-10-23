using System.Text.Json;
using System.Text.Json.Serialization;
using Clyppey.Core.Ffmpeg;
using Clyppey.Core.Settings;

namespace Clyppey.Core.Diagnostics;

/// <summary>Result of inspecting this machine: is there an ffmpeg, and what can it encode?</summary>
public sealed class ClyppeyEnvironment
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string? FfmpegPath { get; init; }

    public string? FfmpegVersion { get; init; }

    public ProbeResults? Probe { get; init; }

    public string? Problem { get; init; }

    public bool IsUsable => FfmpegPath is not null && Probe is { Encoders.Count: > 0 };

    public static ClyppeyEnvironment MissingFfmpeg() => new()
    {
        Problem = "ffmpeg.exe が見つかりません。tools\\ffmpeg\\ffmpeg.exe に配置するか、" +
                  "PATH の通った場所にインストールしてください。"
    };

    /// <summary>Locates ffmpeg and probes encoders, reusing a cache when the binary is unchanged.</summary>
    public static ClyppeyEnvironment Detect(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var ffmpegPath = FfmpegLocator.Find();
        if (ffmpegPath is null)
        {
            Log.Error("ffmpeg.exe が見つかりません。");
            return MissingFfmpeg();
        }

        var version = FfmpegLocator.GetVersion(ffmpegPath);
        Log.Info($"ffmpeg: {ffmpegPath} ({version})");

        var stamp = EncoderProbe.BuildStamp(ffmpegPath);
        var cached = LoadCache();
        if (cached is not null && cached.FfmpegStamp == stamp)
        {
            Log.Info("エンコーダ情報のキャッシュを使用します。");
            return new ClyppeyEnvironment { FfmpegPath = ffmpegPath, FfmpegVersion = version, Probe = cached };
        }

        var results = EncoderProbe.Probe(ffmpegPath, progress, cancellationToken);
        SaveCache(results);
        return new ClyppeyEnvironment { FfmpegPath = ffmpegPath, FfmpegVersion = version, Probe = results };
    }

    /// <summary>Picks the best encoder for the requested codec and family.</summary>
    public EncoderProfile? ResolveEncoder(AppSettings settings)
    {
        if (Probe is null)
        {
            return null;
        }

        var usable = Probe.Encoders
            .Where(e => e.PlainWorks || e.ZeroCopyWorks)
            .ToList();

        if (usable.Count == 0)
        {
            return null;
        }

        EncoderProfile ToProfile(ProbedEncoder e) => new(e.Codec, e.Name, e.Family, e.Hardware);

        // 1) Exact codec match, respecting the requested family.
        var sameCodec = usable.Where(e => e.Codec == settings.Codec).ToList();
        var choice = Pick(sameCodec, settings.Encoder);
        if (choice is not null)
        {
            return ToProfile(choice);
        }

        // 2) Requested family but a different codec.
        if (settings.Encoder != EncoderFamily.Auto)
        {
            choice = Pick(usable.Where(e => e.Family == settings.Encoder).ToList(), settings.Encoder);
            if (choice is not null)
            {
                Log.Warn($"{settings.Codec} をこの環境で使えないため {choice.Name} に切り替えます。");
                return ToProfile(choice);
            }
        }

        // 3) Any H.264, the most compatible fallback.
        choice = Pick(usable.Where(e => e.Codec == CodecKind.H264).ToList(), settings.Encoder);
        if (choice is not null)
        {
            Log.Warn($"要求されたエンコーダを使えないため {choice.Name} に切り替えます。");
            return ToProfile(choice);
        }

        // 4) Whatever works at all.
        choice = usable[0];
        Log.Warn($"フォールバック: {choice.Name}");
        return ToProfile(choice);
    }

    private static ProbedEncoder? Pick(List<ProbedEncoder> candidates, EncoderFamily requested)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        if (requested != EncoderFamily.Auto)
        {
            var exact = candidates.Where(e => e.Family == requested).ToList();
            if (exact.Count > 0)
            {
                candidates = exact;
            }
        }

        // Prefer the zero-copy path, then hardware, then preference order in the probe list.
        return candidates
            .OrderByDescending(e => e.ZeroCopyWorks)
            .ThenByDescending(e => e.Hardware)
            .ThenBy(e => FamilyRank(e.Family))
            .First();
    }

    private static int FamilyRank(EncoderFamily family) => family switch
    {
        EncoderFamily.Nvenc => 0,
        EncoderFamily.Amf => 1,
        EncoderFamily.Qsv => 2,
        EncoderFamily.Software => 3,
        _ => 4
    };

    /// <summary>Human readable summary for the diagnostics page.</summary>
    /// <summary>
    /// Human readable report. <paramref name="t"/> is the UI translator so the words follow the
    /// selected language while Core stays free of UI dependencies.
    /// </summary>
    public string Describe(Func<string, string>? t = null)
    {
        if (Problem is not null)
        {
            return Problem;
        }

        string Word(string key, string fallback) =>
            t?.Invoke(key) is { Length: > 0 } value && !string.Equals(value, key, StringComparison.Ordinal)
                ? value
                : fallback;

        var lines = new List<string>
        {
            $"ffmpeg : {FfmpegPath}",
            $"version: {FfmpegVersion}"
        };

        if (Probe is not null)
        {
            var direct = Word("Diag.EncoderDirect", "OK (GPU direct)");
            var viaCpu = Word("Diag.EncoderCpu", "OK (via CPU)");
            var unavailable = Word("Diag.EncoderUnavailable", "unavailable");

            lines.Add($"probe  : {Probe.ProbedAt:yyyy-MM-dd HH:mm}");
            foreach (var encoder in Probe.Encoders)
            {
                var state = encoder.ZeroCopyWorks ? direct
                    : encoder.PlainWorks ? viaCpu
                    : unavailable;
                lines.Add($"  {encoder.Name,-12} {state,-14} {encoder.Error}");
            }

            if (Probe.CaptureError is not null)
            {
                lines.Add($"capture: {Probe.CaptureError}");
            }
        }

        return string.Join(System.Environment.NewLine, lines);
    }

    private static ProbeResults? LoadCache()
    {
        try
        {
            if (!File.Exists(AppPaths.ProbeCacheFile))
            {
                return null;
            }

            var json = File.ReadAllText(AppPaths.ProbeCacheFile);
            return JsonSerializer.Deserialize<ProbeResults>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            Log.Debug($"エンコーダキャッシュの読み込みに失敗: {ex.Message}");
            return null;
        }
    }

    private static void SaveCache(ProbeResults results)
    {
        try
        {
            AppPaths.EnsureDirectories();
            File.WriteAllText(AppPaths.ProbeCacheFile, JsonSerializer.Serialize(results, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Debug($"エンコーダキャッシュの保存に失敗: {ex.Message}");
        }
    }
}
