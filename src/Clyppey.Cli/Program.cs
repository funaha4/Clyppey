using System.Diagnostics;
using System.Globalization;
using System.Text;
using Clyppey.Core;
using Clyppey.Core.Capture;
using Clyppey.Core.Engine;
using Clyppey.Core.Diagnostics;
using Clyppey.Core.Ffmpeg;
using Clyppey.Core.Native;
using Clyppey.Core.Settings;
using Clyppey.Core.Storage;

namespace Clyppey.Cli;

/// <summary>
/// Headless harness for Clyppey. This is how the engine is verified without the UI,
/// and it doubles as a scripting interface for power users.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        var options = ParseOptions(args.Skip(1).ToArray());

        try
        {
            return command switch
            {
                "probe" => Probe(),
                "paths" => Paths(),
                "monitors" => await MonitorsAsync(options).ConfigureAwait(false),
                "bench" => await BenchAsync(options).ConfigureAwait(false),
                "save" => await SaveAsync(options).ConfigureAwait(false),
                "record" => await RecordAsync(options).ConfigureAwait(false),
                "selftest" => await SelfTestAsync(options).ConfigureAwait(false),
                _ => Help()
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"エラー: {ex.Message}");
            Console.Error.WriteLine(ex);
            return 2;
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            Clyppey - インスタントリプレイ (CLI / 検証ハーネス)

            使い方:
              clyppey probe                     環境とエンコーダを調査
              clyppey monitors [--out <dir>]    モニターごとのプレビュー画像を保存
              clyppey bench  [--seconds 20]     バッファリングの負荷を計測
              clyppey save   [--seconds 30] [--out <file>]   指定秒数を保存
              clyppey record [--seconds 10] [--out <file>]   指定秒数を通常録画
              clyppey selftest [--seconds 25]   エンドツーエンド自動検証

            オプション:
              --monitor   DXGI 出力番号 (default 0)
              --fps       フレームレート (default 60)
              --bitrate   ビットレート Mbps (default 25)
              --no-audio  音声を無効化
            """);
        return 0;
    }

    // ---------------------------------------------------------------- probe

    private static int Paths()
    {
        Console.WriteLine($"AppDataRoot : {AppPaths.AppDataRoot}");
        Console.WriteLine($"  exists    : {Directory.Exists(AppPaths.AppDataRoot)}");
        Console.WriteLine($"LogFile     : {AppPaths.LogFile}  exists={File.Exists(AppPaths.LogFile)}");
        Console.WriteLine($"ProbeCache  : {AppPaths.ProbeCacheFile}  exists={File.Exists(AppPaths.ProbeCacheFile)}");
        Console.WriteLine($"TempDir     : {AppPaths.TempDir}  exists={Directory.Exists(AppPaths.TempDir)}");
        Console.WriteLine($"OutputDir   : {new AppSettings().ResolveOutputDirectory()}");
        Console.WriteLine($"ffmpeg      : {AppPaths.FindFfmpeg()}");
        Console.WriteLine($"OS          : {System.Environment.OSVersion}");
        Console.WriteLine($"LocalAppData: [{System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData)}]");

        try
        {
            AppPaths.EnsureDirectories();
            Console.WriteLine($"EnsureDirectories: OK (exists={Directory.Exists(AppPaths.AppDataRoot)})");
            File.AppendAllText(AppPaths.LogFile, "diagnostic write\n");
            Console.WriteLine($"log write  : OK ({new FileInfo(AppPaths.LogFile).Length} bytes)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"EnsureDirectories: FAILED {ex.GetType().Name}: {ex.Message}");
        }

        return 0;
    }

    private static int Probe()
    {
        var environment = ClyppeyEnvironment.Detect(new Progress<string>(m => Console.WriteLine($"  {m}")));
        Console.WriteLine(environment.Describe());
        Console.WriteLine();
        Console.WriteLine("モニター:");
        foreach (var monitor in MonitorEnumerator.GetMonitors())
        {
            Console.WriteLine($"  [{monitor.Index}] {monitor.Label}");
        }

        if (!environment.IsUsable)
        {
            return 1;
        }

        var settings = new AppSettings();
        var encoder = environment.ResolveEncoder(settings);
        Console.WriteLine();
        Console.WriteLine($"選択されるエンコーダ: {encoder?.DisplayName ?? "なし"} ({encoder?.Name})");
        return encoder is null ? 1 : 0;
    }

    // ---------------------------------------------------------------- monitors

    private static async Task<int> MonitorsAsync(Dictionary<string, string?> options)
    {
        var environment = ClyppeyEnvironment.Detect();
        if (environment.FfmpegPath is null)
        {
            return 1;
        }

        var outDir = options.GetValueOrDefault("out") ?? Path.Combine(AppPaths.TempDir, "monitors");
        Directory.CreateDirectory(outDir);

        foreach (var monitor in MonitorEnumerator.GetMonitors())
        {
            var bytes = await CapturePreview.GrabAsync(environment.FfmpegPath, monitor.Index).ConfigureAwait(false);
            if (bytes is null)
            {
                Console.WriteLine($"[{monitor.Index}] {monitor.Label} -> 取得失敗");
                continue;
            }

            var path = Path.Combine(outDir, $"monitor_{monitor.Index}.jpg");
            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
            Console.WriteLine($"[{monitor.Index}] {monitor.Label} -> {path} ({bytes.Length / 1024} KB)");
        }

        return 0;
    }

    // ---------------------------------------------------------------- bench

    private static async Task<int> BenchAsync(Dictionary<string, string?> options)
    {
        var seconds = options.GetInt("seconds", 20);
        var settings = BuildSettings(options);

        var environment = ClyppeyEnvironment.Detect();
        if (environment.FfmpegPath is null || environment.Probe is null)
        {
            Console.Error.WriteLine(environment.Describe());
            return 1;
        }

        var encoder = environment.ResolveEncoder(settings);
        if (encoder is null)
        {
            Console.Error.WriteLine("利用可能なエンコーダがありません。");
            return 1;
        }

        Console.WriteLine($"エンコーダ : {encoder.DisplayName} ({encoder.Name})");
        Console.WriteLine($"キャプチャ : 出力#{settings.MonitorIndex} {settings.EffectiveFps}fps {settings.EffectiveBitrateMbps}Mbps");
        Console.WriteLine($"バッファ   : {settings.EffectiveBufferSeconds} 秒");
        Console.WriteLine();

        var library = new ClipLibrary();
        using var engine = new ReplayEngine(settings, encoder, environment.FfmpegPath, library);
        engine.Failed += message => Console.Error.WriteLine($"  [!] {message}");

        var sw = Stopwatch.StartNew();
        await engine.StartBufferingAsync().ConfigureAwait(false);

        Console.WriteLine("   経過  バッファ   メモリ  取得fps  drop  CPU%  GPUenc%  実質Mbps");
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            await Task.Delay(1000).ConfigureAwait(false);
            var stats = engine.GetStats();
            Console.WriteLine($"  {sw.Elapsed.TotalSeconds,5:F0}s {stats.BufferedSeconds,7:F1}s " +
                              $"{stats.BufferedMegabytes,7:F1}MB {stats.CaptureFps,7:F1} " +
                              $"{stats.DropFrames,5} {stats.CpuPercent,5:F1} " +
                              $"{(stats.GpuEncodePercent < 0 ? "-" : stats.GpuEncodePercent.ToString("F0")),7} " +
                              $"{stats.BitrateMbps,8:F1}");
        }

        var saveTimer = Stopwatch.StartNew();
        var result = await engine.SaveReplayAsync(10).ConfigureAwait(false);
        saveTimer.Stop();

        await engine.StopAsync().ConfigureAwait(false);

        Console.WriteLine();
        if (result is { Success: true })
        {
            Console.WriteLine($"保存: {result.Path}");
            Console.WriteLine($"  {result.DurationSeconds:F2} 秒 / {result.Bytes / 1048576.0:F1} MB " +
                              $"(書き出し {saveTimer.ElapsedMilliseconds} ms)");
            return 0;
        }

        Console.Error.WriteLine($"保存に失敗: {result?.Error}");
        return 1;
    }

    // ---------------------------------------------------------------- save

    private static async Task<int> SaveAsync(Dictionary<string, string?> options)
    {
        var settings = BuildSettings(options);
        var saveSeconds = options.GetInt("seconds", settings.EffectiveBufferSeconds);
        var outputOverride = options.GetValueOrDefault("out");

        var environment = ClyppeyEnvironment.Detect();
        if (environment.FfmpegPath is null)
        {
            Console.Error.WriteLine(environment.Describe());
            return 1;
        }

        var encoder = environment.ResolveEncoder(settings);
        if (encoder is null)
        {
            Console.Error.WriteLine("利用可能なエンコーダがありません。");
            return 1;
        }

        Console.WriteLine($"エンコーダ: {encoder.DisplayName} / バッファ {settings.EffectiveBufferSeconds} 秒");

        var library = new ClipLibrary();
        using var engine = new ReplayEngine(settings, encoder, environment.FfmpegPath, library);

        await engine.StartBufferingAsync().ConfigureAwait(false);
        var filled = await WaitForBufferAsync(engine, settings.EffectiveBufferSeconds, 60).ConfigureAwait(false);
        Console.WriteLine($"バッファ充填: {filled:F1} 秒");

        var result = await engine.SaveReplayAsync(saveSeconds).ConfigureAwait(false);
        await engine.StopAsync().ConfigureAwait(false);

        if (result is not { Success: true })
        {
            Console.Error.WriteLine($"保存に失敗: {result?.Error}");
            return 1;
        }

        var finalPath = result.Path;
        if (!string.IsNullOrWhiteSpace(outputOverride))
        {
            File.Move(result.Path, outputOverride, overwrite: true);
            finalPath = outputOverride;
        }

        Console.WriteLine($"保存しました: {finalPath} ({result.DurationSeconds:F2} 秒, {result.Bytes / 1048576.0:F1} MB)");
        return 0;
    }

    // ---------------------------------------------------------------- record

    private static async Task<int> RecordAsync(Dictionary<string, string?> options)
    {
        var settings = BuildSettings(options);
        var seconds = options.GetInt("seconds", 10);
        var outputOverride = options.GetValueOrDefault("out");

        var environment = ClyppeyEnvironment.Detect();
        if (environment.FfmpegPath is null)
        {
            Console.Error.WriteLine(environment.Describe());
            return 1;
        }

        var encoder = environment.ResolveEncoder(settings);
        if (encoder is null)
        {
            return 1;
        }

        var library = new ClipLibrary();
        using var engine = new ReplayEngine(settings, encoder, environment.FfmpegPath, library);

        await engine.StartRecordingAsync().ConfigureAwait(false);
        Console.WriteLine($"{seconds} 秒間録画します... ({encoder.DisplayName})");
        await Task.Delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
        var result = await engine.StopRecordingAsync().ConfigureAwait(false);

        if (result is not { Success: true })
        {
            Console.Error.WriteLine($"録画に失敗: {result?.Error}");
            return 1;
        }

        var finalPath = result.Path;
        if (!string.IsNullOrWhiteSpace(outputOverride))
        {
            File.Move(result.Path, outputOverride, overwrite: true);
            finalPath = outputOverride;
        }

        Console.WriteLine($"保存しました: {finalPath} ({result.DurationSeconds:F2} 秒, {result.Bytes / 1048576.0:F1} MB)");
        return 0;
    }

    // ---------------------------------------------------------------- selftest

    private static async Task<int> SelfTestAsync(Dictionary<string, string?> options)
    {
        var seconds = options.GetInt("seconds", 25);
        var settings = BuildSettings(options);
        settings.BufferSeconds = seconds;

        var checks = new List<(string Name, bool Passed, string Detail)>();
        void Check(string name, bool passed, string detail = "") => checks.Add((name, passed, detail));

        Console.WriteLine("=== Clyppey セルフテスト ===");

        // 1. ffmpeg present and encoders available.
        var environment = ClyppeyEnvironment.Detect();
        Check("ffmpeg を検出", environment.FfmpegPath is not null, environment.FfmpegPath ?? "見つかりません");
        if (environment.FfmpegPath is null)
        {
            return Report(checks);
        }

        Console.WriteLine($"  ffmpeg : {environment.FfmpegVersion}");
        Check("エンコーダを検出", environment.Probe?.Encoders.Any(e => e.PlainWorks || e.ZeroCopyWorks) == true,
            $"{environment.Probe?.Encoders.Count(e => e.PlainWorks || e.ZeroCopyWorks) ?? 0} 件利用可能");

        var encoder = environment.ResolveEncoder(settings);
        Check("エンコーダを選択", encoder is not null, encoder?.Name ?? "なし");
        if (encoder is null)
        {
            return Report(checks);
        }

        var zeroCopy = environment.Probe?.Encoders
            .FirstOrDefault(e => e.Name == encoder.Name)?.ZeroCopyWorks == true;
        Check("GPU ゼロコピー経路", zeroCopy || !encoder.Hardware,
            zeroCopy ? "ddagrab -> encoder 直結" : "CPU 経由 (この環境ではこれが最速)");
        Console.WriteLine($"  使用エンコーダ: {encoder.DisplayName}");

        var library = new ClipLibrary();
        using var engine = new ReplayEngine(settings, encoder, environment.FfmpegPath, library);
        string? failure = null;
        engine.Failed += m => failure = m;

        // 2. Buffer fills.
        var startTimer = Stopwatch.StartNew();
        await engine.StartBufferingAsync().ConfigureAwait(false);
        var filled = await WaitForBufferAsync(engine, Math.Min(seconds, 12), 60).ConfigureAwait(false);
        var startupMs = startTimer.ElapsedMilliseconds;
        var stats = engine.GetStats();
        Check("バッファリング開始", stats.State is EngineState.Buffering or EngineState.Recording,
            $"state={stats.State} {failure}");
        Check("バッファにフレームが溜まる", filled >= Math.Min(seconds, 12) * 0.8,
            $"{filled:F1} 秒 / 起動 {startupMs} ms");
        Check("解像度を検出", stats.Width > 0 && stats.Height > 0, $"{stats.Width}x{stats.Height}");
        Check("キャプチャFPSが実用範囲", stats.CaptureFps > settings.EffectiveFps * 0.6,
            $"{stats.CaptureFps:F1} fps");

        // 3. Save.
        var saveTimer = Stopwatch.StartNew();
        var result = await engine.SaveReplayAsync(Math.Min(seconds, filled)).ConfigureAwait(false);
        saveTimer.Stop();
        Check("リプレイを保存", result is { Success: true },
            result is null ? "null" : $"{result.Path} {result.Error}".Trim());
        if (result is not { Success: true })
        {
            await engine.StopAsync().ConfigureAwait(false);
            return Report(checks);
        }

        var expected = Math.Min(seconds, filled);
        Check("保存が完了する", saveTimer.ElapsedMilliseconds < 12000, $"{saveTimer.ElapsedMilliseconds} ms (末尾待ちを含む)");
        Check("長さが要求どおり", Math.Abs(result.DurationSeconds - expected) <= Math.Max(2.0, expected * 0.15),
            $"期待 {expected:F1}s / 実際 {result.DurationSeconds:F2}s");
        Check("ファイルサイズが妥当", result.Bytes > 100_000, $"{result.Bytes / 1024} KB");

        // 4. Independent verification with ffmpeg itself.
        var (duration, decodeError) = await InspectAsync(environment.FfmpegPath, result.Path).ConfigureAwait(false);
        Check("ffmpeg でデコード可能", string.IsNullOrEmpty(decodeError), decodeError);
        Check("コンテナの長さが一致", Math.Abs(duration - result.DurationSeconds) <= 0.6,
            $"コンテナ {duration:F2}s");
        Check("MP4 インデックスが存在", HasMoov(result.Path), "moov ボックスあり (faststart は廃止)");

        var finalStats = engine.GetStats();
        Check("CPU 使用率が低い", finalStats.CpuPercent < 25, $"{finalStats.CpuPercent:F1}%");
        Console.WriteLine($"  バッファ: {finalStats.BufferedSeconds:F1} 秒 / {finalStats.BufferedMegabytes:F1} MB");
        Console.WriteLine($"  GPU エンコーダ使用率: {(finalStats.GpuEncodePercent < 0 ? "取得不可" : finalStats.GpuEncodePercent.ToString("F0") + "%")}");
        Console.WriteLine($"  音声: {(finalStats.AudioActive ? "有効" : "無効")}");

        await engine.StopAsync().ConfigureAwait(false);
        Check("停止できる", engine.GetStats().State == EngineState.Stopped, engine.GetStats().State.ToString());

        Console.WriteLine($"  出力: {result.Path}");
        return Report(checks);
    }

    private static int Report(List<(string Name, bool Passed, string Detail)> checks)
    {
        Console.WriteLine();
        var failed = 0;
        foreach (var (name, passed, detail) in checks)
        {
            Console.WriteLine($"  [{(passed ? "PASS" : "FAIL")}] {name,-28} {detail}");
            if (!passed)
            {
                failed++;
            }
        }

        Console.WriteLine();
        Console.WriteLine(failed == 0
            ? $"=== すべて成功 ({checks.Count} 項目) ==="
            : $"=== {failed} 件失敗 / {checks.Count} 項目 ===");
        return failed == 0 ? 0 : 1;
    }

    private static async Task<(double Duration, string Error)> InspectAsync(string ffmpegPath, string path)
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
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(path);
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("null");
        psi.ArgumentList.Add("-");

        using var process = Process.Start(psi)!;
        var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);

        var duration = 0.0;
        foreach (var line in stderr.Split('\n'))
        {
            if (!line.Contains("Duration:", StringComparison.Ordinal))
            {
                continue;
            }

            var part = line.Split("Duration:")[1].Split(',')[0].Trim();
            if (TimeSpan.TryParse(part, CultureInfo.InvariantCulture, out var span))
            {
                duration = span.TotalSeconds;
            }
        }

        // "-v error" pass: any decode problem shows up here.
        var verify = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        foreach (var arg in new[] { "-hide_banner", "-v", "error", "-i", path, "-f", "null", "-" })
        {
            verify.ArgumentList.Add(arg);
        }

        using var verifyProcess = Process.Start(verify)!;
        var errors = await verifyProcess.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await verifyProcess.WaitForExitAsync().ConfigureAwait(false);

        var errorText = string.Join(" / ", errors.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).Take(3));
        return (duration, errorText);
    }

    /// <summary>moov may sit at either end now that faststart is gone; look at both.</summary>
    private static bool HasMoov(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var probe = (int)Math.Min(stream.Length, 2L * 1024 * 1024);
            var head = new byte[probe];
            stream.Read(head, 0, probe);
            if (Encoding.ASCII.GetString(head).Contains("moov", StringComparison.Ordinal))
            {
                return true;
            }

            stream.Seek(Math.Max(0, stream.Length - probe), SeekOrigin.Begin);
            var tail = new byte[probe];
            var read = stream.Read(tail, 0, probe);
            return Encoding.ASCII.GetString(tail, 0, read).Contains("moov", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsFastStart(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[Math.Min(512 * 1024, (int)Math.Min(stream.Length, int.MaxValue))];
            var read = stream.Read(buffer, 0, buffer.Length);
            var text = Encoding.ASCII.GetString(buffer, 0, read);
            var moov = text.IndexOf("moov", StringComparison.Ordinal);
            var mdat = text.IndexOf("mdat", StringComparison.Ordinal);
            return moov >= 0 && mdat >= 0 && moov < mdat;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<double> WaitForBufferAsync(ReplayEngine engine, double targetSeconds, int timeoutSeconds)
    {
        var sw = Stopwatch.StartNew();
        var last = 0.0;
        while (sw.Elapsed.TotalSeconds < timeoutSeconds)
        {
            var state = engine.GetStats();
            last = state.BufferedSeconds;
            if (state.State == EngineState.Error)
            {
                break;
            }

            if (last >= targetSeconds)
            {
                break;
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        return last;
    }

    private static AppSettings BuildSettings(Dictionary<string, string?> options)
    {
        var settings = SettingsStore.Load();
        if (options.TryGetValue("monitor", out var monitor) && int.TryParse(monitor, out var index))
        {
            settings.MonitorIndex = index;
        }

        if (options.TryGetValue("fps", out var fps) && int.TryParse(fps, out var fpsValue))
        {
            settings.Fps = fpsValue;
        }

        if (options.TryGetValue("bitrate", out var bitrate) && int.TryParse(bitrate, out var bitrateValue))
        {
            settings.BitrateMbps = bitrateValue;
        }

        if (options.TryGetValue("buffer", out var buffer) && int.TryParse(buffer, out var bufferValue))
        {
            settings.BufferSeconds = bufferValue;
        }

        if (options.ContainsKey("no-audio"))
        {
            settings.CaptureAudio = false;
        }

        if (options.ContainsKey("no-mic"))
        {
            settings.CaptureMicrophone = false;
        }

        if (options.ContainsKey("no-disk-buffer"))
        {
            settings.BufferOnDisk = false;
        }

        if (options.TryGetValue("codec", out var codec) && Enum.TryParse<CodecKind>(codec, true, out var codecValue))
        {
            settings.Codec = codecValue;
        }

        return settings;
    }

    private static Dictionary<string, string?> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = arg[2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                result[key] = args[++i];
            }
            else
            {
                result[key] = null;
            }
        }

        return result;
    }
}

internal static class OptionExtensions
{
    public static int GetInt(this Dictionary<string, string?> options, string key, int fallback) =>
        options.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;
}
