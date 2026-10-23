using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clyppey.Core.Settings;

/// <summary>Everything the app persists between runs.</summary>
public sealed class AppSettings
{
    // ---- Capture -------------------------------------------------------
    /// <summary>DXGI output index (Desktop Duplication enumeration order).</summary>
    public int MonitorIndex { get; set; }

    public int Fps { get; set; } = 60;

    public CodecKind Codec { get; set; } = CodecKind.H264;

    public RateMode Rate { get; set; } = RateMode.Cbr;

    /// <summary>Target bitrate in Mbps (CBR/VBR modes).</summary>
    public int BitrateMbps { get; set; } = 25;

    /// <summary>CQ / QP level for CQ mode (lower = better quality).</summary>
    public int QualityLevel { get; set; } = 21;

    public bool DrawMouse { get; set; } = true;

    public EncoderFamily Encoder { get; set; } = EncoderFamily.Auto;

    // ---- Replay buffer -------------------------------------------------
    /// <summary>How many seconds of gameplay the ring buffer holds.</summary>
    public int BufferSeconds { get; set; } = 60;

    /// <summary>Hard cap on ring buffer RAM usage.</summary>
    public int MaxBufferMegabytes { get; set; } = 3072;

    /// <summary>
    /// Keep the encoded replay buffer on disk instead of in RAM. This is how NVIDIA's Instant
    /// Replay stays at a few hundred MB: memory use no longer grows with the buffer length.
    /// Costs a few MB/s of disk writes while buffering.
    /// </summary>
    public bool BufferOnDisk { get; set; } = true;

    /// <summary>Start buffering as soon as the app launches.</summary>
    public bool AutoStartBuffering { get; set; } = true;

    // ---- Output --------------------------------------------------------
    public string OutputDirectory { get; set; } = string.Empty;

    public string FileNameTemplate { get; set; } = "Clyppey_{0:yyyy-MM-dd_HH-mm-ss}";

    // ---- Audio ---------------------------------------------------------
    /// <summary>Record what the speakers play (WASAPI loopback).</summary>
    public bool CaptureAudio { get; set; } = true;

    /// <summary>Record a microphone alongside the game audio.</summary>
    public bool CaptureMicrophone { get; set; } = true;

    /// <summary>WASAPI render device id; null means "default device".</summary>
    public string? AudioDeviceId { get; set; }

    /// <summary>WASAPI capture device id; null means "default device".</summary>
    public string? MicrophoneDeviceId { get; set; }

    /// <summary>Microphone gain in percent (100 = unity, 200 = +6 dB).</summary>
    public int MicrophoneVolume { get; set; } = 100;

    /// <summary>Manual A/V alignment tweak in milliseconds.</summary>
    public int AudioSyncOffsetMs { get; set; }

    /// <summary>
    /// Per-device microphone latency correction in milliseconds. Positive means the microphone
    /// track sounds late, so earlier audio is used for it.
    /// </summary>
    public int MicrophoneDelayMs { get; set; }

    // ---- Interface -----------------------------------------------------
    /// <summary>UI language: "en" or "ja".</summary>
    public string Language { get; set; } = "en";

    /// <summary>Quality preset; keeps the main screen to one understandable choice.</summary>
    public QualityPreset Quality { get; set; } = QualityPreset.High;

    // ---- Hotkeys -------------------------------------------------------
    public string HotkeySaveReplay { get; set; } = "Alt+F10";

    public string HotkeyToggleRecord { get; set; } = "Alt+F9";

    public string HotkeyToggleBuffer { get; set; } = "Alt+F8";

    // ---- Behaviour -----------------------------------------------------
    public bool StartMinimized { get; set; }

    public bool PlaySoundOnSave { get; set; } = true;

    public bool OpenFolderOnSave { get; set; }

    public bool StartWithWindows { get; set; }

    /// <summary>
    /// The folder clips are written to. Falls back to the app data folder when the configured
    /// (or default Videos) location is not writable, rather than failing every save.
    /// </summary>
    public string ResolveOutputDirectory()
    {
        var preferred = string.IsNullOrWhiteSpace(OutputDirectory) ? AppPaths.DefaultOutputDir : OutputDirectory;
        if (AppPaths.TryEnsureWritable(preferred))
        {
            return preferred;
        }

        if (AppPaths.TryEnsureWritable(AppPaths.FallbackClipsDir))
        {
            Log.Warn($"既定の保存先に書き込めないため {AppPaths.FallbackClipsDir} を使用します。");
            return AppPaths.FallbackClipsDir;
        }

        Log.Warn($"保存先を確保できないため {AppPaths.AppDataRoot} を使用します。");
        return AppPaths.AppDataRoot;
    }

        [JsonIgnore]
    public int EffectiveBufferSeconds => Math.Clamp(BufferSeconds, 5, 3600);

        [JsonIgnore]
    public int EffectiveFps => Math.Clamp(Fps, 15, 240);

    /// <summary>Bitrate implied by the quality preset (or the manual value in Custom mode).</summary>
        [JsonIgnore]
    public int EffectiveBitrateMbps => Quality switch
    {
        QualityPreset.Low => 10,
        QualityPreset.Standard => 20,
        QualityPreset.High => 35,
        QualityPreset.Max => 60,
        _ => Math.Clamp(BitrateMbps, 2, 200)
    };

        [JsonIgnore]
    public double EffectiveMicrophoneVolume => Math.Clamp(MicrophoneVolume, 0, 200) / 100.0;

    /// <summary>
    /// Rough RAM footprint of the replay buffer, in megabytes:
    /// bitrate x seconds / 8, plus a little for container overhead.
    /// </summary>
        [JsonIgnore]
    public double EstimatedBufferMegabytes =>
        (BufferOnDisk ? 0 : VideoMegabytes) + AudioMegabytes;

    /// <summary>Encoded video held in RAM.</summary>
    public double VideoMegabytes =>
        EffectiveBitrateMbps * 1_000_000.0 / 8.0 * EffectiveBufferSeconds / (1024 * 1024) * 1.06;

    /// <summary>
    /// PCM held in RAM. 48 kHz stereo 16-bit per enabled source; the estimate used to ignore this,
    /// which is why the app looked lighter than it actually was.
    /// </summary>
    public double AudioMegabytes
    {
        get
        {
            var sources = (CaptureAudio ? 1 : 0) + (CaptureMicrophone ? 1 : 0);
            return sources * 48_000.0 * 2 * 2 * (EffectiveBufferSeconds + 15) / (1024 * 1024);
        }
    }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (settings is not null)
                {
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"設定の読み込みに失敗したため既定値を使います: {ex.Message}");
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            AppPaths.EnsureDirectories();
            var json = JsonSerializer.Serialize(settings, Options);
            var temp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, AppPaths.SettingsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("設定の保存に失敗しました", ex);
        }
    }
}
