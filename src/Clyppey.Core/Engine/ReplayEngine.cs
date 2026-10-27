using System.Diagnostics;
using Clyppey.Core.Audio;
using Clyppey.Core.Capture;
using Clyppey.Core.Ffmpeg;
using Clyppey.Core.Monitoring;
using Clyppey.Core.Mp4;
using Clyppey.Core.Settings;
using Clyppey.Core.Storage;

namespace Clyppey.Core.Engine;

public enum EngineState
{
    Stopped,
    Buffering,
    Recording,
    Saving,
    Error
}

/// <summary>Everything the UI needs to draw one frame of status.</summary>
public sealed record ReplayStats
{
    public EngineState State { get; init; }

    public string EncoderName { get; init; } = string.Empty;

    public bool Hardware { get; init; }

    public int Fps { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public double BufferedSeconds { get; init; }

    public double BufferTargetSeconds { get; init; }

    public double BufferedMegabytes { get; init; }

    public double CaptureFps { get; init; }

    public long DropFrames { get; init; }

    public long DupFrames { get; init; }

    public double CpuPercent { get; init; }

    public double GpuEncodePercent { get; init; }

    public double BitrateMbps { get; init; }

    public bool AudioActive { get; init; }

    public bool DesktopAudioActive { get; init; }

    public bool MicrophoneActive { get; init; }

    /// <summary>Live microphone input level, 0..1 (for the level meter).</summary>
    public float MicrophoneLevel { get; init; }

    public double RecordingSeconds { get; init; }

    public bool MemoryCapped { get; init; }

    public bool IsLive => State is EngineState.Buffering or EngineState.Recording;
}

public sealed record ClipSaveResult(bool Success, string Path, double DurationSeconds, long Bytes, string Mode, string? Error);

/// <summary>
/// The instant-replay engine. One ffmpeg process captures the desktop on the GPU and streams
/// keyframe-aligned fragments into RAM; saving cuts those fragments out and stream-copies them
/// into a normal MP4. No re-encoding ever happens on the save path, which is why a clip is
/// ready almost instantly and why capturing costs the game essentially nothing.
/// </summary>
public sealed class ReplayEngine : IDisposable
{
    private readonly AppSettings _settings;
    private readonly FragmentRing _ring = new();
    private readonly FragmentedMp4Demuxer _demuxer = new();
    private readonly AudioCapture _desktopAudio = new(AudioKind.Desktop);
    private readonly AudioCapture _microphone = new(AudioKind.Microphone);
    private readonly ClipLibrary _library;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly object _gate = new();

    private CaptureSession? _session;
    private DiskFragmentStore? _store;
    private PerfSampler? _perf;
    private CaptureSink? _sink;
    private CancellationTokenSource? _watchCts;
    private FileStream? _recordStream;
    private string? _recordTempPath;
    private DateTime _recordStart;
    private double _firstFragmentArrival;
    private double _firstFragmentDuration;
    private double _captureStartWall;
    private double _firstVideoStart = -1;
    private double _latestVideoEnd;
    private bool _disposed;

    public ReplayEngine(AppSettings settings, EncoderProfile encoder, string ffmpegPath, ClipLibrary library)
    {
        _settings = settings;
        Encoder = encoder;
        FfmpegPath = ffmpegPath;
        _library = library;

        _demuxer.InitSegmentReady += OnInitSegmentReady;
        _demuxer.FragmentReady += OnFragmentReady;

        _ring.MaxSeconds = settings.EffectiveBufferSeconds;
        _ring.MaxBytes = Math.Max(64, settings.MaxBufferMegabytes) * 1024L * 1024L;
    }

    public EncoderProfile Encoder { get; }

    public string FfmpegPath { get; }

    public EngineState State { get; private set; } = EngineState.Stopped;

    public ClipLibrary Library => _library;

    public event Action<EngineState>? StateChanged;

    public event Action<ClipSaveResult>? ClipSaved;

    public event Action<string>? Failed;

    // ---------------------------------------------------------------- public API

    public async Task StartBufferingAsync()
    {
        await _busy.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State is EngineState.Buffering or EngineState.Recording)
            {
                return;
            }

            SetState(EngineState.Stopped);
            BeginSession();
            SetState(EngineState.Buffering);
        }
        catch (Exception ex)
        {
            Log.Error("バッファリングを開始できませんでした", ex);
            SetState(EngineState.Error);
            Failed?.Invoke($"バッファリングを開始できませんでした: {ex.Message}");
        }
        finally
        {
            _busy.Release();
        }
    }

    public async Task StartRecordingAsync()
    {
        await _busy.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State is EngineState.Recording or EngineState.Buffering)
            {
                return;
            }

            AppPaths.EnsureDirectories();

            // Tear down anything running first: EndSession() closes the previous recording file,
            // so the new stream must be created afterwards.
            EndSession();

            _recordTempPath = Path.Combine(AppPaths.TempDir, $"rec_{Guid.NewGuid():N}.mp4");
            _recordStream = new FileStream(
                _recordTempPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);

            StartCapture();
            _recordStart = DateTime.Now;
            SetState(EngineState.Recording);
            Log.Info($"録画を開始しました: {_recordTempPath}");
        }
        catch (Exception ex)
        {
            Log.Error("録画を開始できませんでした", ex);
            SetState(EngineState.Error);
            Failed?.Invoke($"録画を開始できませんでした: {ex.Message}");
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>Saves the last <paramref name="seconds"/> of buffered gameplay.</summary>
    public async Task<ClipSaveResult?> SaveReplayAsync(double? seconds = null)
    {
        if (State is not (EngineState.Buffering or EngineState.Recording))
        {
            Log.Warn("バッファリング中ではないため保存できません。");
            return null;
        }

        await _busy.WaitAsync().ConfigureAwait(false);
        var previous = State;
        try
        {
            SetState(EngineState.Saving);
            var pressWall = Clock.Now;

            // The encoder writes a fragment only once the following one is complete, so the
            // newest second or two of gameplay is still inside ffmpeg when the hotkey is pressed.
            // Wait for it instead of silently losing the moment the user wanted to keep.
            var deadline = pressWall + 6.0;
            while (Clock.Now < deadline)
            {
                double newest;
                lock (_gate)
                {
                    newest = _ring.NewestContentEndWall;
                }

                if (newest >= pressWall - 0.2)
                {
                    break;
                }

                await Task.Delay(80).ConfigureAwait(false);
            }

            // Queued fragment writes must be on disk before the clip is assembled.
            _store?.WaitForPending();

            RingSnapshot snapshot;
            lock (_gate)
            {
                snapshot = _ring.Take(seconds ?? _settings.EffectiveBufferSeconds);
            }

            if (snapshot.IsEmpty)
            {
                return new ClipSaveResult(false, string.Empty, 0, 0, "replay", "バッファに映像がありません");
            }

            // TEMP diagnostics for A/V alignment investigations.
            var f0 = snapshot.Fragments[0];
            var fN = snapshot.Fragments[^1];
            Log.Debug($"[diag] n={snapshot.Fragments.Count} dur={snapshot.Duration:F3} " +
                     $"wall={snapshot.WallStart:F3}..{snapshot.WallEnd:F3} " +
                     $"first(s={f0.Start:F3} d={f0.Duration:F3} a={f0.ArrivalSeconds:F3}) " +
                     $"last(s={fN.Start:F3} d={fN.Duration:F3} a={fN.ArrivalSeconds:F3}) " +
                     $"arrivalSpan={fN.ArrivalSeconds - f0.ArrivalSeconds:F3} contentSpan={fN.Start + fN.Duration - f0.Start:F3} " +
                     $"captureStart={_captureStartWall:F3} audioAnchor={_desktopAudio.StartClock:F3} " +
                     $"arrivalMinusStart0={f0.ArrivalSeconds - f0.Start:F3} deltaVsCapture={(f0.ArrivalSeconds - f0.Start) - _captureStartWall:F3}");

            var outputPath = BuildClipPath(isRecording: false);

            // The audio window must line up with the picture's content time. Deriving it from
            // "capture start + a constant" breaks as soon as ffmpeg's start-up takes longer.
            // Instead subtract the observed muxer buffering from the first fragment's arrival:
            // ffmpeg holds two fragments before writing them (measured: arrival - content start
            // is ~2 x fragment length), so this self-calibrates on any machine.
            var lead = snapshot.Fragments[0];
            var windowStart = lead.ArrivalSeconds - (FragmentFlushFragments * lead.Duration);
            var audio = BuildAudioMix(windowStart, windowStart + snapshot.Duration);

            var stopwatch = Stopwatch.StartNew();
            var result = await ClipWriter.WriteAsync(FfmpegPath, snapshot, outputPath, audio)
                .ConfigureAwait(false);
            stopwatch.Stop();

            DeleteAudioTemp(audio);

            if (result.Success)
            {
                Log.Info($"リプレイを保存しました: {result.OutputPath} " +
                         $"({result.Duration:F1} 秒, {result.Bytes / 1048576.0:F1} MB, {stopwatch.ElapsedMilliseconds} ms)");
                _library.Add(new ClipEntry(result.OutputPath, "replay", snapshot.Duration, result.Bytes, DateTimeOffset.Now));
                var saveResult = new ClipSaveResult(true, result.OutputPath, snapshot.Duration, result.Bytes, "replay", null);
                ClipSaved?.Invoke(saveResult);
                return saveResult;
            }

            Log.Error($"リプレイの保存に失敗しました: {result.Error}");
            var failed = new ClipSaveResult(false, outputPath, 0, 0, "replay", result.Error);
            ClipSaved?.Invoke(failed);
            return failed;
        }
        catch (Exception ex)
        {
            Log.Error("リプレイの保存中にエラーが発生しました", ex);
            return new ClipSaveResult(false, string.Empty, 0, 0, "replay", ex.Message);
        }
        finally
        {
            SetState(previous);
            _busy.Release();
        }
    }

    /// <summary>Stops manual recording and returns the finished file.</summary>
    public async Task<ClipSaveResult?> StopRecordingAsync()
    {
        await _busy.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State is not EngineState.Recording)
            {
                return null;
            }

            SetState(EngineState.Saving);

            var endWall = Clock.Now;

            // Audio must be pulled while the capture is still alive, and the encoder's own
            // reported out_time is the most accurate length of what has actually been encoded.
            // Same self-calibrating anchor as the replay path: start from the first fragment's
            // arrival and subtract the muxer's buffering, so a slow start-up cannot skew it.
            var recordStart = _firstFragmentArrival > 0
                ? _firstFragmentArrival - ((FragmentFlushFragments - 1.0) * _firstFragmentDuration)
                : _captureStartWall + AudioPipelineOffsetSeconds;
            var wavPath = BuildAudioMix(recordStart, endWall + (recordStart - _captureStartWall));
            var temp = _recordTempPath;
            var encodedSeconds = _session?.Progress.OutTimeSeconds ?? 0;

            EndSession();

            var videoSpan = _latestVideoEnd - Math.Max(0, _firstVideoStart);
            var wallSpan = (DateTime.Now - _recordStart).TotalSeconds;
            // out_time is sampled a few times a second, so allow a small tail rather than
            // risking cutting the last frame off.
            var duration = encodedSeconds > 0.2
                ? encodedSeconds + 0.1
                : videoSpan > 0.2 ? videoSpan : wallSpan;
            Log.Debug($"録画の長さ: out_time={encodedSeconds:F3} span={videoSpan:F3} wall={wallSpan:F3} -> {duration:F3}");

            if (string.IsNullOrEmpty(temp) || !File.Exists(temp))
            {
                DeleteAudioTemp(wavPath);
                return new ClipSaveResult(false, string.Empty, 0, 0, "record", "録画ファイルが見つかりません");
            }

            var outputPath = BuildClipPath(isRecording: true);
            var args = FfmpegCommandBuilder.BuildFileRemuxArgs(
                temp, outputPath, wavPath, withFastStart: false, durationSeconds: duration);
            var (ok, error) = await ProcessRunner.RunAsync(FfmpegPath, args).ConfigureAwait(false);

            DeleteQuietly(temp);
            DeleteAudioTemp(wavPath);
            _recordTempPath = null;

            if (!ok)
            {
                Log.Error($"録画の保存に失敗しました: {error}");
                return new ClipSaveResult(false, outputPath, duration, 0, "record", error);
            }

            var bytes = new FileInfo(outputPath).Exists ? new FileInfo(outputPath).Length : 0;
            Log.Info($"録画を保存しました: {outputPath} ({duration:F1} 秒)");
            _library.Add(new ClipEntry(outputPath, "record", duration, bytes, DateTimeOffset.Now));
            var result = new ClipSaveResult(true, outputPath, duration, bytes, "record", null);
            ClipSaved?.Invoke(result);
            return result;
        }
        catch (Exception ex)
        {
            Log.Error("録画の停止中にエラーが発生しました", ex);
            return new ClipSaveResult(false, string.Empty, 0, 0, "record", ex.Message);
        }
        finally
        {
            SetState(EngineState.Stopped);
            _busy.Release();
        }
    }

    public async Task StopAsync()
    {
        await _busy.WaitAsync().ConfigureAwait(false);
        try
        {
            EndSession();
            SetState(EngineState.Stopped);
        }
        finally
        {
            _busy.Release();
        }
    }

    public ReplayStats GetStats()
    {
        double bufferedSeconds;
        long bytes;
        bool capped;
        lock (_gate)
        {
            bufferedSeconds = _ring.BufferedSeconds;
            bytes = _ring.Bytes;
            capped = _ring.EvictedForMemory > 0;
        }

        var progress = _session?.Progress;
        var bitrate = 0.0;
        if (progress is { TotalBytes: > 0, OutTimeSeconds: > 0.5 })
        {
            bitrate = progress.TotalBytes * 8.0 / progress.OutTimeSeconds / 1_000_000.0;
        }

        return new ReplayStats
        {
            State = State,
            EncoderName = Encoder.DisplayName,
            Hardware = Encoder.Hardware,
            Fps = _settings.EffectiveFps,
            Width = _demuxer.VideoWidth,
            Height = _demuxer.VideoHeight,
            BufferedSeconds = bufferedSeconds,
            BufferTargetSeconds = _settings.EffectiveBufferSeconds,
            BufferedMegabytes = bytes / 1048576.0,
            CaptureFps = progress?.Fps ?? 0,
            DropFrames = progress?.DropFrames ?? 0,
            DupFrames = progress?.DupFrames ?? 0,
            CpuPercent = _perf?.CpuPercent ?? 0,
            GpuEncodePercent = _perf?.GpuEncoderPercent ?? -1,
            BitrateMbps = bitrate,
            AudioActive = _desktopAudio.IsRunning,
            DesktopAudioActive = _desktopAudio.IsRunning,
            MicrophoneActive = _microphone.IsRunning,
            MicrophoneLevel = _microphone.PeakLevel,
            RecordingSeconds = State == EngineState.Recording ? (DateTime.Now - _recordStart).TotalSeconds : 0,
            MemoryCapped = capped
        };
    }

    /// <summary>Applies buffer-size changes without restarting the pipeline.</summary>
    public void ApplySettings()
    {
        lock (_gate)
        {
            _ring.MaxSeconds = _settings.EffectiveBufferSeconds;
            _ring.MaxBytes = Math.Max(64, _settings.MaxBufferMegabytes) * 1024L * 1024L;
        }
    }

    // ---------------------------------------------------------------- internals

    private void BeginSession()
    {
        EndSession();
        StartCapture();
    }

    /// <summary>Starts the ffmpeg process. Assumes any previous session has been torn down.</summary>
    private void StartCapture()
    {
        var options = CaptureOptions.Create(_settings, Encoder, FfmpegPath);

        _demuxer.Reset();
        lock (_gate)
        {
            _ring.Clear();
            _ring.MaxSeconds = _settings.EffectiveBufferSeconds;
            _ring.MaxBytes = Math.Max(64, _settings.MaxBufferMegabytes) * 1024L * 1024L;
        }

        _firstFragmentArrival = 0;
        _firstFragmentDuration = 0;
        _captureStartWall = Clock.Now;

        if (_settings.BufferOnDisk)
        {
            DiskFragmentStore.RemoveStale();
            WarnIfDiskIsTight();
            _store = new DiskFragmentStore();
            _ring.Evicted += OnFragmentEvicted;
            Log.Info($"リプレイバッファはディスクに保存します: {_store.Folder}");
        }
        _firstVideoStart = -1;
        _latestVideoEnd = 0;
        _sink = new CaptureSink(_demuxer, _gate, _recordStream);
        _session = CaptureSession.Start(options, _sink);
        _session.LogLine += OnFfmpegLog;

        if (_settings.CaptureAudio)
        {
            _desktopAudio.Start(_settings.AudioDeviceId, _settings.EffectiveBufferSeconds);
        }

        if (_settings.CaptureMicrophone)
        {
            _microphone.Start(_settings.MicrophoneDeviceId, _settings.EffectiveBufferSeconds);
        }

        _perf = new PerfSampler(_session.ProcessId);
        _perf.Start();
        StartWatcher(_session);
    }

    private void EndSession()
    {
        _watchCts?.Cancel();
        _watchCts?.Dispose();
        _watchCts = null;

        _perf?.Dispose();
        _perf = null;

        var session = _session;
        _session = null;
        if (session is not null)
        {
            session.LogLine -= OnFfmpegLog;
            session.Dispose();
        }

        _sink = null;

        _ring.Evicted -= OnFragmentEvicted;
        _ring.Clear();
        _store?.Dispose();
        _store = null;

        try
        {
            _desktopAudio.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug($"デスクトップ音声の終了処理: {ex.Message}");
        }

        try
        {
            _microphone.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug($"マイクの終了処理: {ex.Message}");
        }

        try
        {
            _recordStream?.Flush();
            _recordStream?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug($"録画ファイルのクローズ: {ex.Message}");
        }

        _recordStream = null;
    }

    private void StartWatcher(CaptureSession session)
    {
        _watchCts = new CancellationTokenSource();
        var token = _watchCts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (session.HasExited)
                {
                    if (State is EngineState.Buffering or EngineState.Recording)
                    {
                        Log.Error($"キャプチャプロセスが予期せず終了しました (exit={session.ExitCode})");
                        _ = RestartCaptureAsync(session.ExitCode ?? 0);
                    }

                    return;
                }
            }
        }, token);
    }

    private void OnInitSegmentReady(byte[] init)
    {
        lock (_gate)
        {
            _ring.SetInitSegment(init);
        }

        Log.Info($"映像ストリーム初期化: {_demuxer.VideoWidth}x{_demuxer.VideoHeight} " +
                 $"(timescale {_demuxer.Timescale})");

        if (State != EngineState.Recording)
        {
            SetState(EngineState.Buffering);
        }
    }

    private void OnFragmentReady(FragmentChunk chunk)
    {
        var fps = Math.Max(1, _settings.EffectiveFps);
        var storedPath = _store?.Write(chunk.Data);
        var fragment = new VideoFragment
        {
            Data = storedPath is null ? chunk.Data : Array.Empty<byte>(),
            FilePath = storedPath,
            SizeBytes = chunk.Data.Length,
            Start = chunk.StartSeconds,
            Duration = chunk.SampleCount > 0 ? chunk.SampleCount / (double)fps : 1.0 / fps,
            SampleCount = chunk.SampleCount,
            ArrivalSeconds = chunk.ArrivalSeconds
        };

        if (_firstFragmentArrival <= 0)
        {
            // Arrival is the fragment's end, so the video timeline starts one duration earlier.
            _firstFragmentArrival = Math.Max(0, chunk.ArrivalSeconds - fragment.Duration);
            _firstFragmentDuration = fragment.Duration;
        }

        if (_firstVideoStart < 0)
        {
            _firstVideoStart = fragment.Start;
        }

        _latestVideoEnd = fragment.Start + fragment.Duration;

        lock (_gate)
        {
            _ring.Add(fragment);
        }
    }

    private void OnFragmentEvicted(VideoFragment fragment) => _store?.Delete(fragment.FilePath);

    /// <summary>
    /// Rebuilds the capture after the desktop duplication was lost (display change, lock screen,
    /// exclusive fullscreen, driver reset). Retries with growing delays before giving up.
    /// </summary>
    private async Task RestartCaptureAsync(int exitCode)
    {
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt)).ConfigureAwait(false);

            if (State is not (EngineState.Buffering or EngineState.Recording))
            {
                return; // the user stopped it meanwhile
            }

            try
            {
                Log.Info($"キャプチャを再接続します (試行 {attempt})");
                await StopAsync().ConfigureAwait(false);
                await StartBufferingAsync().ConfigureAwait(false);
                Log.Info("キャプチャを再接続しました");
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"再接続に失敗しました (試行 {attempt}): {ex.Message}");
            }
        }

        Log.Error($"キャプチャを再接続できませんでした (最後の終了コード {exitCode})");
        SetState(EngineState.Error);
        Failed?.Invoke("キャプチャを再接続できませんでした。バッファを停止してから再度開始してください。");
    }

    /// <summary>
    /// The disk buffer is capped at buffer-length x bitrate, but that is still gigabytes. Say so
    /// instead of filling the drive quietly.
    /// </summary>
    private void WarnIfDiskIsTight()
    {
        try
        {
            var root = Path.GetPathRoot(AppPaths.TempDir);
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            var drive = new DriveInfo(root);
            var needed = DiskFragmentStore.EstimateBytesPerSecond(_settings.EffectiveBitrateMbps)
                         * _settings.EffectiveBufferSeconds;
            const long Margin = 2L * 1024 * 1024 * 1024;

            if (drive.AvailableFreeSpace < needed + Margin)
            {
                Log.Warn($"ディスクの空きが不足気味です: 必要 約 {needed / 1024.0 / 1024.0 / 1024.0:F1} GB / " +
                         $"空き {drive.AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0:F1} GB");
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"空き容量を確認できません: {ex.Message}");
        }
    }

    private void OnFfmpegLog(string line)
    {
        if (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("cannot", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("unable", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn($"ffmpeg: {line}");
        }
        else
        {
            Log.Debug($"ffmpeg: {line}");
        }
    }

    private FfmpegCommandBuilder.AudioMix BuildAudioMix(double startWall, double endWall)
    {
        if (endWall <= startWall)
        {
            return new FfmpegCommandBuilder.AudioMix(null, null, 1.0);
        }

        string? desktop = null;
        string? microphone = null;
        var offset = _settings.AudioSyncOffsetMs;

        if (_settings.CaptureAudio && _desktopAudio.IsRunning)
        {
            var path = Path.Combine(AppPaths.TempDir, $"audio_desktop_{Guid.NewGuid():N}.wav");
            desktop = _desktopAudio.WriteWindowWav(startWall, endWall, path, offset);
        }

        if (_settings.CaptureMicrophone && _microphone.IsRunning)
        {
            var path = Path.Combine(AppPaths.TempDir, $"audio_mic_{Guid.NewGuid():N}.wav");
            var micOffset = offset - _settings.MicrophoneDelayMs;
            microphone = _microphone.WriteWindowWav(startWall, endWall, path, micOffset);
        }

        return new FfmpegCommandBuilder.AudioMix(desktop, microphone, _settings.EffectiveMicrophoneVolume);
    }

    /// <summary>
    /// How many fragments ffmpeg buffers before writing them out. Measured: a fragment's bytes
    /// arrive about two fragment lengths after its first frame was captured, so the audio window
    /// starts that far back from the arrival time.
    /// </summary>
    private const double FragmentFlushFragments = 2.0;

    /// <summary>Measured delay from starting ffmpeg to the first frame (record path).</summary>
    private const double AudioPipelineOffsetSeconds = 0.195;

    private static void DeleteAudioTemp(FfmpegCommandBuilder.AudioMix audio)
    {
        DeleteQuietly(audio.DesktopWavPath);
        DeleteQuietly(audio.MicrophoneWavPath);
    }

    private string BuildClipPath(bool isRecording)
    {
        var directory = _settings.ResolveOutputDirectory();
        var stem = FormatName(isRecording);
        var path = Path.Combine(directory, stem + ".mp4");

        var counter = 1;
        while (File.Exists(path))
        {
            path = Path.Combine(directory, $"{stem}_{counter++}.mp4");
        }

        return path;
    }

    private string FormatName(bool isRecording)
    {
        string name;
        try
        {
            name = string.Format(_settings.FileNameTemplate, DateTime.Now);
        }
        catch (FormatException)
        {
            name = $"Clyppey_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
        }

        if (isRecording)
        {
            name += "_rec";
        }

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(name) ? $"Clyppey_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}" : name;
    }

    private static void DeleteQuietly(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Debug($"一時ファイルを削除できません: {path} ({ex.Message})");
        }
    }

    private void SetState(EngineState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        try
        {
            StateChanged?.Invoke(state);
        }
        catch (Exception ex)
        {
            Log.Debug($"StateChanged ハンドラでエラー: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        EndSession();
        _desktopAudio.Dispose();
        _microphone.Dispose();
        _busy.Dispose();
    }

    /// <summary>Feeds the demuxer and (in recording mode) the file on disk from one stream.</summary>
    private sealed class CaptureSink : ICaptureSink
    {
        private readonly FragmentedMp4Demuxer _demuxer;
        private readonly object _gate;
        private readonly FileStream? _file;

        public CaptureSink(FragmentedMp4Demuxer demuxer, object gate, FileStream? file)
        {
            _demuxer = demuxer;
            _gate = gate;
            _file = file;
        }

        public void Write(ReadOnlySpan<byte> data)
        {
            if (_file is not null)
            {
                _file.Write(data);
            }

            lock (_gate)
            {
                _demuxer.Push(data);
            }
        }

        public void Complete(string? error)
        {
            lock (_gate)
            {
                _demuxer.Flush();
            }

            try
            {
                _file?.Flush();
            }
            catch (Exception)
            {
                // Ignore.
            }
        }
    }
}
