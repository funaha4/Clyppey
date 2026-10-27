using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clyppey.App.Localization;
using Clyppey.App.Platform;
using Clyppey.Core;
using Clyppey.Core.Audio;
using Clyppey.Core.Capture;
using Clyppey.Core.Diagnostics;
using Clyppey.Core.Engine;
using Clyppey.Core.Ffmpeg;
using Clyppey.Core.Native;
using Clyppey.Core.Settings;
using Clyppey.Core.Storage;

namespace Clyppey.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(name);
        return true;
    }
}

public sealed class MonitorChoice : ObservableObject
{
    private BitmapImage? _thumbnail;

    public required int Index { get; init; }

    private string _label = string.Empty;

    public string Label
    {
        get => _label;
        set => Set(ref _label, value);
    }

    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        set => Set(ref _thumbnail, value);
    }
}

public sealed record CodecChoice(CodecKind Value, string Display)
{
    public override string ToString() => Display;
}

public sealed record RateChoice(RateMode Value, string Display)
{
    public override string ToString() => Display;
}

/// <summary>Everything the main window binds to.</summary>
public sealed class MainViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions SettingsJson = new();

    private readonly AppSettings _settings;
    private readonly ClipLibrary _library;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _statsTimer;
    private readonly DispatcherTimer _animationTimer;
    private readonly DispatcherTimer _restartTimer;

    private ClyppeyEnvironment? _environment;
    private ReplayEngine? _engine;
    private string _captureSignature = string.Empty;
    private string _lastSavedJson = string.Empty;
    private DateTime _toastUntil = DateTime.MinValue;
    private bool _initialized;
    private bool _busy;

    private double _bufferPercent;
    private double _micLevel;
    private double _micLevelTarget;

    private static readonly SolidColorBrush IdleBrush = Frozen("#8A93A5");
    private static readonly SolidColorBrush ReadyBrush = Frozen("#2DD4BF");
    private static readonly SolidColorBrush RecordBrush = Frozen("#FF6B4A");
    private static readonly SolidColorBrush SaveBrush = Frozen("#F5C542");
    private static readonly SolidColorBrush ErrorBrush = Frozen("#FF6B4A");

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public MainViewModel(AppSettings settings, ClipLibrary library)
    {
        _settings = settings;
        _library = library;
        _dispatcher = Dispatcher.CurrentDispatcher;

        FpsOptions = new ObservableCollection<int> { 30, 60, 120, 144, 240 };
        CodecOptions = new ObservableCollection<CodecChoice>
        {
            new(CodecKind.H264, "H.264"),
            new(CodecKind.Hevc, "HEVC / H.265"),
            new(CodecKind.Av1, "AV1")
        };
        RateOptions = new ObservableCollection<RateChoice>
        {
            new(RateMode.Cbr, "CBR"),
            new(RateMode.Vbr, "VBR"),
            new(RateMode.Cq, "CQ (quality)")
        };

        RecentClips = new ObservableCollection<ClipEntry>();
        AllClips = new ObservableCollection<ClipEntry>();
        Monitors = new ObservableCollection<MonitorChoice>();
        AudioDevices = new ObservableCollection<AudioDeviceInfo>();
        MicrophoneDevices = new ObservableCollection<AudioDeviceInfo>();

        _selectedCodec = CodecOptions.FirstOrDefault(c => c.Value == settings.Codec) ?? CodecOptions[0];
        _selectedRate = RateOptions.FirstOrDefault(r => r.Value == settings.Rate) ?? RateOptions[0];
        _selectedFps = settings.EffectiveFps;

        _statsTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _statsTimer.Tick += (_, _) => RefreshStats();

        // 30 fps animation loop so the buffer bar and mic meter move smoothly rather than jumping.
        _animationTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _animationTimer.Tick += (_, _) => AnimateValues();

        _restartTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(700) };
        _restartTimer.Tick += async (_, _) => await ApplyPendingPipelineChangeAsync().ConfigureAwait(true);

        Loc.Changed += OnLanguageChanged;
    }

    // ---------------------------------------------------------------- bindable state

    public AppSettings Settings => _settings;

    public ObservableCollection<int> FpsOptions { get; }

    public ObservableCollection<CodecChoice> CodecOptions { get; }

    public ObservableCollection<RateChoice> RateOptions { get; }

    public ObservableCollection<MonitorChoice> Monitors { get; }

    public ObservableCollection<AudioDeviceInfo> AudioDevices { get; }

    public ObservableCollection<AudioDeviceInfo> MicrophoneDevices { get; }

    public ObservableCollection<ClipEntry> RecentClips { get; }

    public ObservableCollection<ClipEntry> AllClips { get; }

    public string AppVersion => Clyppey.Core.AppInfo.DisplayVersion;

    public string AppTitle => Loc.F("App.Title");

    public string Language => Loc.Current;

    // ---------------------------------------------------------------- text (localised)

    private string _stateLabel = string.Empty;
    private string _stateShort = string.Empty;
    private string _stateDetail = string.Empty;
    private string _bufferRangeLabel = string.Empty;
    private string _bufferExplain = string.Empty;
    private string _bufferedOfText = string.Empty;
    private string _memoryEstimateText = string.Empty;
    private string _saveHotkeyLabel = string.Empty;
    private string _recordButtonText = string.Empty;
    private string _bufferButtonText = string.Empty;
    private string _captureFpsText = "—";
    private string _dropText = "0";
    private string _cpuText = "—";
    private string _gpuText = "—";
    private string _bitrateText = "—";
    private string _resolutionText = "—";
    private string _desktopAudioText = string.Empty;
    private string _micText = string.Empty;
    private string _memoryText = "0 MB";
    private string _encoderBadge = string.Empty;
    private string _hotkeyWarning = string.Empty;
    private string _environmentProblem = string.Empty;
    private string _diagText = string.Empty;
    private string _logText = string.Empty;
    private string _toastText = string.Empty;
    private string _outputDirectoryDisplay = string.Empty;

    public string StateLabel { get => _stateLabel; private set => Set(ref _stateLabel, value); }

    public string StateShort { get => _stateShort; private set => Set(ref _stateShort, value); }

    public string StateDetail { get => _stateDetail; private set => Set(ref _stateDetail, value); }

    public string BufferRangeLabel { get => _bufferRangeLabel; private set => Set(ref _bufferRangeLabel, value); }

    public string BufferExplain { get => _bufferExplain; private set => Set(ref _bufferExplain, value); }

    public string BufferedOfText { get => _bufferedOfText; private set => Set(ref _bufferedOfText, value); }

    public string MemoryEstimateText { get => _memoryEstimateText; private set => Set(ref _memoryEstimateText, value); }

    public string SaveHotkeyLabel { get => _saveHotkeyLabel; private set => Set(ref _saveHotkeyLabel, value); }

    public string RecordButtonText { get => _recordButtonText; private set => Set(ref _recordButtonText, value); }

    public string BufferButtonText { get => _bufferButtonText; private set => Set(ref _bufferButtonText, value); }

    public string CaptureFpsText { get => _captureFpsText; private set => Set(ref _captureFpsText, value); }

    public string DropText { get => _dropText; private set => Set(ref _dropText, value); }

    public string CpuText { get => _cpuText; private set => Set(ref _cpuText, value); }

    public string GpuText { get => _gpuText; private set => Set(ref _gpuText, value); }

    public string BitrateText { get => _bitrateText; private set => Set(ref _bitrateText, value); }

    public string ResolutionText { get => _resolutionText; private set => Set(ref _resolutionText, value); }

    public string DesktopAudioText { get => _desktopAudioText; private set => Set(ref _desktopAudioText, value); }

    public string MicText { get => _micText; private set => Set(ref _micText, value); }

    public string MemoryText { get => _memoryText; private set => Set(ref _memoryText, value); }

    public string EncoderBadge { get => _encoderBadge; private set => Set(ref _encoderBadge, value); }

    public string HotkeyWarning { get => _hotkeyWarning; private set => Set(ref _hotkeyWarning, value); }

    public bool HasHotkeyWarning => HotkeyWarning.Length > 0;

    public string EnvironmentProblem { get => _environmentProblem; private set => Set(ref _environmentProblem, value); }

    public bool HasEnvironmentProblem => EnvironmentProblem.Length > 0;

    public string DiagnosticsText { get => _diagText; private set => Set(ref _diagText, value); }

    public string LogText { get => _logText; private set => Set(ref _logText, value); }

    public string ToastText { get => _toastText; private set => Set(ref _toastText, value); }

    public bool ToastVisible => _toastUntil > DateTime.Now && ToastText.Length > 0;

    public string OutputDirectoryDisplay { get => _outputDirectoryDisplay; private set => Set(ref _outputDirectoryDisplay, value); }

    private Brush _stateBrush = IdleBrush;

    public Brush StateBrush
    {
        get => _stateBrush;
        private set => Set(ref _stateBrush, value);
    }

    public Brush NavStateBrush => _stateBrush;

    private bool _isRecording;
    private bool _isLive;
    private bool _canSave;
    private bool _hasMicrophone;

    public bool IsRecording { get => _isRecording; private set { if (Set(ref _isRecording, value)) { Raise(nameof(RecordButtonText)); } } }

    public bool IsLive { get => _isLive; private set => Set(ref _isLive, value); }

    public bool CanSave { get => _canSave; private set => Set(ref _canSave, value); }

    public bool HasMicrophone { get => _hasMicrophone; private set => Set(ref _hasMicrophone, value); }

    private bool _isAdvancedOpen;

    /// <summary>The settings page stays minimal; advanced controls are hidden until asked for.</summary>
    public bool IsAdvancedOpen
    {
        get => _isAdvancedOpen;
        set => Set(ref _isAdvancedOpen, value);
    }

    public double BufferPercent
    {
        get => _bufferPercent;
        private set => Set(ref _bufferPercent, value);
    }

    public double MicLevel
    {
        get => _micLevel;
        private set => Set(ref _micLevel, value);
    }

    // ---------------------------------------------------------------- selection proxies

    private MonitorChoice? _selectedMonitor;

    public MonitorChoice? SelectedMonitor
    {
        get => _selectedMonitor;
        set
        {
            if (Set(ref _selectedMonitor, value) && value is not null)
            {
                _settings.MonitorIndex = value.Index;
                SchedulePipelineChange();
            }
        }
    }

    private CodecChoice _selectedCodec;

    public CodecChoice SelectedCodec
    {
        get => _selectedCodec;
        set
        {
            if (Set(ref _selectedCodec, value) && value is not null)
            {
                _settings.Codec = value.Value;
                _settings.Quality = QualityPreset.Custom;
                RaiseQualityPills();
                SchedulePipelineChange();
            }
        }
    }

    private RateChoice _selectedRate;

    public RateChoice SelectedRate
    {
        get => _selectedRate;
        set
        {
            if (Set(ref _selectedRate, value) && value is not null)
            {
                _settings.Rate = value.Value;
                SchedulePipelineChange();
            }
        }
    }

    private int _selectedFps;

    public int SelectedFps
    {
        get => _selectedFps;
        set
        {
            if (Set(ref _selectedFps, value))
            {
                _settings.Fps = value;
                RefreshBufferTexts();
                SchedulePipelineChange();
            }
        }
    }

    private AudioDeviceInfo? _selectedAudioDevice;

    public AudioDeviceInfo? SelectedAudioDevice
    {
        get => _selectedAudioDevice;
        set
        {
            if (Set(ref _selectedAudioDevice, value))
            {
                _settings.AudioDeviceId = value?.Id;
                SchedulePipelineChange();
            }
        }
    }

    private AudioDeviceInfo? _selectedMicrophone;

    public AudioDeviceInfo? SelectedMicrophone
    {
        get => _selectedMicrophone;
        set
        {
            if (Set(ref _selectedMicrophone, value))
            {
                _settings.MicrophoneDeviceId = value?.Id;
                SchedulePipelineChange();
            }
        }
    }

    // ---- updates -------------------------------------------------------

    private string _updateStatus = string.Empty;
    private bool _updateAvailable;
    private bool _updateBusy;
    private Clyppey.Core.Update.UpdateInfo? _pendingUpdate;

    public string UpdateStatus => _updateStatus.Length > 0 ? _updateStatus : Loc.T("Settings.UpdateIdle");

    private void SetUpdateStatus(string value)
    {
        _updateStatus = value;
        Raise(nameof(UpdateStatus));
    }

    public bool UpdateAvailable
    {
        get => _updateAvailable;
        private set
        {
            if (Set(ref _updateAvailable, value))
            {
                Raise(nameof(CanApplyUpdate));
            }
        }
    }

    public bool CanCheckUpdate => !_updateBusy;

    public bool CanApplyUpdate => _updateAvailable && !_updateBusy;

    public async Task CheckForUpdatesAsync()
    {
        if (_updateBusy)
        {
            return;
        }

        _updateBusy = true;
        Raise(nameof(CanCheckUpdate));
        Raise(nameof(CanApplyUpdate));
        SetUpdateStatus(Loc.T("Settings.UpdateChecking"));

        try
        {
            _pendingUpdate = await Clyppey.Core.Update.UpdateService
                .CheckAsync(Clyppey.Core.AppInfo.Version, CancellationToken.None)
                .ConfigureAwait(true);

            UpdateAvailable = _pendingUpdate is not null;
            SetUpdateStatus(_pendingUpdate is null
                ? Loc.F("Settings.UpdateUpToDate", AppVersion)
                : Loc.F("Settings.UpdateAvailable", "v" + _pendingUpdate.Version,
                    $"{_pendingUpdate.SizeBytes / 1024.0 / 1024.0:F0} MB"));
            Log.Info($"更新確認: 現在 {AppVersion} / 最新 v{_pendingUpdate?.Version ?? AppInfo.Version}");
        }
        catch (Exception ex)
        {
            Log.Warn($"更新確認に失敗: {ex.Message}");
            SetUpdateStatus(Loc.F("Settings.UpdateFailed", ex.Message));
        }
        finally
        {
            _updateBusy = false;
            Raise(nameof(CanCheckUpdate));
            Raise(nameof(CanApplyUpdate));
        }
    }

    public async Task ApplyUpdateAsync()
    {
        if (_pendingUpdate is null || _updateBusy)
        {
            return;
        }

        _updateBusy = true;
        Raise(nameof(CanCheckUpdate));
        Raise(nameof(CanApplyUpdate));

        try
        {
            var progress = new Progress<double>(fraction =>
                SetUpdateStatus(Loc.F("Settings.UpdateDownloading", fraction * 100)));

            var zip = await Clyppey.Core.Update.UpdateService
                .DownloadAsync(_pendingUpdate, progress, CancellationToken.None)
                .ConfigureAwait(true);

            SetUpdateStatus(Loc.T("Settings.UpdateInstalling"));
            var files = Clyppey.Core.Update.UpdateService.Extract(zip);
            var installDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            Clyppey.Core.Update.UpdateService.StartSwap(files, installDirectory, Environment.ProcessId);

            await Task.Delay(400).ConfigureAwait(true);
            Application.Current?.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Warn($"アップデートに失敗: {ex.Message}");
            SetUpdateStatus(Loc.F("Settings.UpdateFailed", ex.Message));
            _updateBusy = false;
            Raise(nameof(CanCheckUpdate));
            Raise(nameof(CanApplyUpdate));
        }
    }

    /// <summary>Forwarded when a display/session change invalidates the duplication.</summary>
    public void RequestCaptureReconnect() => _engine?.RequestReconnect();

    /// <summary>Registers (or removes) the per-user Run entry for this executable.</summary>
    public bool StartWithWindows
    {
        get => _settings.StartWithWindows;
        set
        {
            if (_settings.StartWithWindows == value)
            {
                return;
            }

            _settings.StartWithWindows = value;
            if (!Platform.Autostart.SetEnabled(value))
            {
                Log.Warn("自動起動を設定できませんでした");
            }

            Raise();
        }
    }

    /// <summary>Disk-backed replay buffer: RAM stops growing with the buffer length.</summary>
    public bool BufferOnDisk
    {
        get => _settings.BufferOnDisk;
        set
        {
            if (_settings.BufferOnDisk == value)
            {
                return;
            }

            _settings.BufferOnDisk = value;
            Raise();
            Raise(nameof(MemoryEstimateText));
            SchedulePipelineChange();
        }
    }

    public bool DesktopAudioEnabled
    {
        get => _settings.CaptureAudio;
        set
        {
            if (_settings.CaptureAudio == value)
            {
                return;
            }

            _settings.CaptureAudio = value;
            Raise();
            SchedulePipelineChange();
        }
    }

    public bool MicrophoneEnabled
    {
        get => _settings.CaptureMicrophone;
        set
        {
            if (_settings.CaptureMicrophone == value)
            {
                return;
            }

            _settings.CaptureMicrophone = value;
            Raise();
            SchedulePipelineChange();
        }
    }

    public int MicrophoneVolume
    {
        get => _settings.MicrophoneVolume;
        set
        {
            var clamped = Math.Clamp(value, 0, 200);
            if (_settings.MicrophoneVolume == clamped)
            {
                return;
            }

            _settings.MicrophoneVolume = clamped;
            Raise();
            Raise(nameof(MicrophoneVolumeText));
        Raise(nameof(MicrophoneDelayText));
        }
    }

    public string MicrophoneVolumeText => $"{_settings.MicrophoneVolume} %";

    /// <summary>Positive = the mic track sounds late and is pulled earlier.</summary>
    public int MicrophoneDelayValue
    {
        get => _settings.MicrophoneDelayMs;
        set
        {
            var clamped = Math.Clamp(value, -1000, 1000);
            if (_settings.MicrophoneDelayMs == clamped)
            {
                return;
            }

            _settings.MicrophoneDelayMs = clamped;
            Raise();
        }
    }

    public string MicrophoneDelayText => _settings.MicrophoneDelayMs + " ms";

    public string BufferSecondsLabel => Loc.F("Common.SecondsValue", _settings.BufferSeconds);

    /// <summary>
    /// Value proxies: AppSettings is a plain object, so sliders need a property that raises
    /// PropertyChanged for the numeric readout next to them to stay in sync.
    /// </summary>
    public int BufferSecondsValue
    {
        get => _settings.BufferSeconds;
        set
        {
            var clamped = Math.Clamp(value, 5, 3600);
            if (_settings.BufferSeconds == clamped)
            {
                return;
            }

            _settings.BufferSeconds = clamped;
            Raise();
            RefreshBufferTexts();
            SchedulePipelineChange();
        }
    }

    public int BitrateMbpsValue
    {
        get => _settings.BitrateMbps;
        set
        {
            var clamped = Math.Clamp(value, 2, 200);
            if (_settings.BitrateMbps == clamped)
            {
                return;
            }

            _settings.BitrateMbps = clamped;
            _settings.Quality = QualityPreset.Custom;
            Raise();
            Raise(nameof(Settings));
            RaiseQualityPills();
            RefreshBufferTexts();
            SchedulePipelineChange();
        }
    }

    public int QualityLevelValue
    {
        get => _settings.QualityLevel;
        set
        {
            var clamped = Math.Clamp(value, 1, 51);
            if (_settings.QualityLevel == clamped)
            {
                return;
            }

            _settings.QualityLevel = clamped;
            Raise();
            SchedulePipelineChange();
        }
    }

    public int SyncOffsetMs
    {
        get => _settings.AudioSyncOffsetMs;
        set
        {
            var clamped = Math.Clamp(value, -500, 500);
            if (_settings.AudioSyncOffsetMs == clamped)
            {
                return;
            }

            _settings.AudioSyncOffsetMs = clamped;
            Raise();
        }
    }

    public bool OutputDirectoryIsDefault => string.IsNullOrWhiteSpace(_settings.OutputDirectory);

    public string OutputDirectoryEdit
    {
        get => _settings.OutputDirectory;
        set
        {
            if (string.Equals(_settings.OutputDirectory, value, StringComparison.Ordinal))
            {
                return;
            }

            _settings.OutputDirectory = value;
            Raise();
            Raise(nameof(OutputDirectoryIsDefault));
            RefreshOutputPath();

            if (OutputDirectoryIsOverridden)
            {
                ShowToast(Loc.F("Toast.OutputFallback", _resolvedOutputDir));
            }
        }
    }

    // quality presets -------------------------------------------------

    public bool QualityLow
    {
        get => _settings.Quality == QualityPreset.Low;
        set => SetQuality(QualityPreset.Low, value);
    }

    public bool QualityStandard
    {
        get => _settings.Quality == QualityPreset.Standard;
        set => SetQuality(QualityPreset.Standard, value);
    }

    public bool QualityHigh
    {
        get => _settings.Quality == QualityPreset.High;
        set => SetQuality(QualityPreset.High, value);
    }

    public bool QualityMax
    {
        get => _settings.Quality == QualityPreset.Max;
        set => SetQuality(QualityPreset.Max, value);
    }

    private void SetQuality(QualityPreset preset, bool selected)
    {
        if (!selected || _settings.Quality == preset)
        {
            return;
        }

        _settings.Quality = preset;
        _settings.BitrateMbps = _settings.EffectiveBitrateMbps;
        RaiseQualityPills();
        Raise(nameof(Settings));
        RefreshBufferTexts();
        SchedulePipelineChange();
    }

    private void RaiseQualityPills()
    {
        Raise(nameof(QualityLow));
        Raise(nameof(QualityStandard));
        Raise(nameof(QualityHigh));
        Raise(nameof(QualityMax));
    }

    // language --------------------------------------------------------

    public bool IsEnglish
    {
        get => Loc.Current == Loc.English;
        set
        {
            if (value)
            {
                SetLanguage(Loc.English);
            }
        }
    }

    public bool IsJapanese
    {
        get => Loc.Current == Loc.Japanese;
        set
        {
            if (value)
            {
                SetLanguage(Loc.Japanese);
            }
        }
    }

    public void SetLanguage(string language)
    {
        var normalized = Loc.Normalize(language);
        if (_settings.Language == normalized && Loc.Current == normalized)
        {
            return;
        }

        _settings.Language = normalized;
        Loc.Set(normalized);
        SettingsStore.Save(_settings);
    }

    private void OnLanguageChanged()
    {
        Raise(nameof(IsEnglish));
        Raise(nameof(IsJapanese));
        Raise(nameof(Language));
        Raise(nameof(AppTitle));
        UpdateMonitorLabels();
        RefreshSettingsBindings();
        RefreshBufferTexts();
        RefreshStats();
    }

    // ---------------------------------------------------------------- lifecycle

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        LoadMonitors();
        LoadAudioDevices();
        RefreshLibrary();
        RefreshOutputPath();
        RefreshBufferTexts();
        _lastSavedJson = SerializeSettings();

        await RedetectAsync().ConfigureAwait(true);

        if (_settings.AutoStartBuffering)
        {
            await StartEngineAsync(recording: false).ConfigureAwait(true);
        }

        _ = LoadThumbnailsAsync();
        _statsTimer.Start();
        _animationTimer.Start();
        RefreshStats();
    }

    private string SerializeSettings()
    {
        try
        {
            return JsonSerializer.Serialize(_settings, SettingsJson);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Settings used to be written only when the user pressed Apply, so closing the app or
    /// letting Windows log off could silently lose them. Now every change is persisted.
    /// </summary>
    private void PersistSettingsIfChanged()
    {
        var json = SerializeSettings();
        if (json.Length == 0 || json == _lastSavedJson)
        {
            return;
        }

        SettingsStore.Save(_settings);
        _lastSavedJson = json;
    }

    private List<MonitorInfo> _monitorInfos = new();

    private static string FormatMonitorLabel(MonitorInfo info)
    {
        var word = Loc.Current == Loc.Japanese ? "モニター" : "Monitor";
        var primary = info.IsPrimary ? (Loc.Current == Loc.Japanese ? " (プライマリ)" : " (primary)") : string.Empty;
        return $"{word} {info.Index + 1} · {info.Width}×{info.Height}{primary}";
    }

    private void UpdateMonitorLabels()
    {
        foreach (var choice in Monitors)
        {
            var info = _monitorInfos.FirstOrDefault(m => m.Index == choice.Index);
            if (info is not null)
            {
                choice.Label = FormatMonitorLabel(info);
            }
        }
    }

    private void LoadMonitors()
    {
        _monitorInfos = MonitorEnumerator.GetMonitors().ToList();
        Monitors.Clear();
        foreach (var monitor in _monitorInfos)
        {
            Monitors.Add(new MonitorChoice { Index = monitor.Index, Label = FormatMonitorLabel(monitor) });
        }

        SelectedMonitor = Monitors.FirstOrDefault(m => m.Index == _settings.MonitorIndex) ?? Monitors.FirstOrDefault();
    }

    private async Task LoadThumbnailsAsync()
    {
        if (_environment?.FfmpegPath is null)
        {
            return;
        }

        foreach (var monitor in Monitors.ToList())
        {
            try
            {
                var bytes = await CapturePreview.GrabAsync(_environment.FfmpegPath, monitor.Index).ConfigureAwait(true);
                if (bytes is null)
                {
                    continue;
                }

                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new MemoryStream(bytes);
                image.EndInit();
                image.Freeze();
                monitor.Thumbnail = image;
            }
            catch (Exception ex)
            {
                Log.Debug($"モニター {monitor.Index} のプレビュー取得に失敗: {ex.Message}");
            }
        }
    }

    private void LoadAudioDevices()
    {
        AudioDevices.Clear();
        MicrophoneDevices.Clear();

        foreach (var device in AudioCapture.GetDevices(AudioKind.Desktop))
        {
            AudioDevices.Add(device);
        }

        foreach (var device in AudioCapture.GetDevices(AudioKind.Microphone))
        {
            MicrophoneDevices.Add(device);
        }

        HasMicrophone = MicrophoneDevices.Count > 0;
        SelectedAudioDevice = AudioDevices.FirstOrDefault(d => d.Id == _settings.AudioDeviceId) ?? AudioDevices.FirstOrDefault();
        SelectedMicrophone = MicrophoneDevices.FirstOrDefault(d => d.Id == _settings.MicrophoneDeviceId) ?? MicrophoneDevices.FirstOrDefault();
    }

    public void RefreshLibrary()
    {
        var clips = _library.All;
        AllClips.Clear();
        RecentClips.Clear();
        foreach (var clip in clips)
        {
            AllClips.Add(clip);
            if (RecentClips.Count < 5)
            {
                RecentClips.Add(clip);
            }
        }
    }

    public async Task RedetectAsync()
    {
        DiagnosticsText = "…";
        try
        {
            var environment = await Task.Run(() => ClyppeyEnvironment.Detect()).ConfigureAwait(true);
            _environment = environment;

            EnvironmentProblem = environment.IsUsable ? string.Empty : (environment.Problem ?? Loc.T("Toast.NeedEncoder"));
            DiagnosticsText = environment.Describe(Loc.T);

            var encoder = environment.ResolveEncoder(_settings);
            if (encoder is null)
            {
                EncoderBadge = Loc.T("Toast.NeedEncoder");
                EnvironmentProblem = Loc.T("Toast.NeedEncoder") + " " + Loc.T("Toast.NeedEncoderHint");
            }
            else
            {
                EncoderBadge = $"{encoder.DisplayName} · {_settings.EffectiveFps}fps";
            }

            Raise(nameof(EnvironmentProblem));
            Raise(nameof(HasEnvironmentProblem));
            Raise(nameof(EncoderBadge));
        }
        catch (Exception ex)
        {
            Log.Error("環境診断に失敗しました", ex);
            DiagnosticsText = ex.Message;
            EnvironmentProblem = ex.Message;
            Raise(nameof(EnvironmentProblem));
            Raise(nameof(HasEnvironmentProblem));
        }
    }

    // ---------------------------------------------------------------- engine

    private EncoderProfile? ResolveEncoder() => _environment?.ResolveEncoder(_settings);

    private string CaptureSignature(EncoderProfile encoder) =>
        $"{encoder.Name}|{_settings.MonitorIndex}|{_settings.EffectiveFps}|{_settings.Rate}|" +
        $"{_settings.EffectiveBitrateMbps}|{_settings.QualityLevel}|{_settings.DrawMouse}|" +
        $"{_settings.CaptureAudio}|{_settings.CaptureMicrophone}|{_settings.MicrophoneDeviceId}|{_settings.AudioDeviceId}";

    /// <summary>Queues a pipeline rebuild so rapid changes (dragging a slider) coalesce.</summary>
    private void SchedulePipelineChange()
    {
        if (_engine is null)
        {
            return;
        }

        _restartTimer.Stop();
        _restartTimer.Start();
    }

    private async Task ApplyPendingPipelineChangeAsync()
    {
        _restartTimer.Stop();
        PersistSettingsIfChanged();

        if (_engine is null || _environment?.FfmpegPath is null)
        {
            return;
        }

        var encoder = ResolveEncoder();
        if (encoder is null)
        {
            return;
        }

        if (CaptureSignature(encoder) == _captureSignature)
        {
            _engine.ApplySettings();
            return;
        }

        var wasRecording = _engine.GetStats().State == EngineState.Recording;
        await _engine.StopAsync().ConfigureAwait(true);
        await StartEngineAsync(wasRecording).ConfigureAwait(true);
        ShowToast(Loc.T("Settings.Saved"));
    }

    private async Task StartEngineAsync(bool recording)
    {
        if (_environment?.FfmpegPath is null)
        {
            return;
        }

        var encoder = ResolveEncoder();
        if (encoder is null)
        {
            return;
        }

        _captureSignature = CaptureSignature(encoder);

        _engine?.Dispose();
        _engine = new ReplayEngine(_settings, encoder, _environment.FfmpegPath, _library);
        _engine.ClipSaved += OnClipSaved;
        _engine.Failed += OnEngineFailed;

        if (recording)
        {
            await _engine.StartRecordingAsync().ConfigureAwait(true);
        }
        else
        {
            await _engine.StartBufferingAsync().ConfigureAwait(true);
        }

        RefreshStats();
    }

    private void OnClipSaved(ClipSaveResult result)
    {
        _dispatcher.BeginInvoke(() =>
        {
            RefreshLibrary();
            RefreshOutputPath();
            if (result.Success)
            {
                ShowToast(Loc.F("Toast.Saved", result.DurationSeconds.ToString("0.0"), (result.Bytes / 1048576.0).ToString("0.0")));
                if (_settings.PlaySoundOnSave)
                {
                    System.Media.SystemSounds.Asterisk.Play();
                }

                if (_settings.OpenFolderOnSave)
                {
                    _ = RevealClipAsync(result.Path);
                }
            }
            else
            {
                ShowToast(Loc.F("Toast.SaveFailed", result.Error ?? "?"));
            }
        });
    }

    private void OnEngineFailed(string message)
    {
        _dispatcher.BeginInvoke(() =>
        {
            ShowToast(Loc.F("Toast.EncoderFailed", message));
            RefreshStats();
        });
    }

    public async Task ToggleBufferAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            if (_engine is not null && _engine.GetStats().IsLive)
            {
                await _engine.StopAsync().ConfigureAwait(true);
            }
            else if (_engine is null)
            {
                await StartEngineAsync(recording: false).ConfigureAwait(true);
            }
            else
            {
                await _engine.StartBufferingAsync().ConfigureAwait(true);
            }

            RefreshStats();
        }
        finally
        {
            _busy = false;
        }
    }

    public async Task ToggleRecordAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            if (_engine is null)
            {
                await StartEngineAsync(recording: true).ConfigureAwait(true);
            }
            else if (_engine.GetStats().State == EngineState.Recording)
            {
                var result = await _engine.StopRecordingAsync().ConfigureAwait(true);
                if (result is { Success: true })
                {
                    ShowToast(Loc.F("Toast.RecordSaved", result.DurationSeconds.ToString("0.0")));
                }
                else if (result is { Success: false })
                {
                    ShowToast(Loc.F("Toast.SaveFailed", result.Error ?? "?"));
                }

                if (_settings.AutoStartBuffering)
                {
                    await StartEngineAsync(recording: false).ConfigureAwait(true);
                }
            }
            else
            {
                await _engine.StartRecordingAsync().ConfigureAwait(true);
            }

            RefreshStats();
        }
        finally
        {
            _busy = false;
        }
    }

    public async Task SaveReplayAsync()
    {
        if (_busy || _engine is null)
        {
            return;
        }

        _busy = true;
        try
        {
            var result = await _engine.SaveReplayAsync(_settings.EffectiveBufferSeconds).ConfigureAwait(true);
            if (result is { Success: false })
            {
                ShowToast(Loc.F("Toast.SaveFailed", result.Error ?? "?"));
            }
        }
        finally
        {
            _busy = false;
        }
    }

    public void Shutdown()
    {
        _statsTimer.Stop();
        _animationTimer.Stop();
        _restartTimer.Stop();
        Loc.Changed -= OnLanguageChanged;
        SettingsStore.Save(_settings);

        if (_engine is not null)
        {
            try
            {
                _engine.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn($"終了処理でエラー: {ex.Message}");
            }

            _engine = null;
        }
    }

    // ---------------------------------------------------------------- stats & animation

    private void AnimateValues()
    {
        var target = _statsTargetPercent;
        BufferPercent += (target - BufferPercent) * 0.22;
        if (Math.Abs(target - BufferPercent) < 0.2)
        {
            BufferPercent = target;
        }

        var micTarget = _micLevelTarget;
        // Fast attack, slow release, the way an audio meter behaves.
        MicLevel = micTarget > MicLevel ? micTarget : MicLevel + (micTarget - MicLevel) * 0.12;
    }

    private double _statsTargetPercent;

    public void RefreshStats()
    {
        var stats = _engine?.GetStats();
        var seconds = _settings.EffectiveBufferSeconds;

        if (stats is null)
        {
            StateLabel = Loc.T("State.Stopped");
            StateShort = Loc.T("State.Stopped");
            StateDetail = Loc.T("State.StoppedDetail");
            StateBrush = IdleBrush;
            IsLive = false;
            IsRecording = false;
            CanSave = false;
            _statsTargetPercent = 0;
        }
        else
        {
            (StateLabel, StateShort, StateDetail, StateBrush) = Describe(stats);
            IsLive = stats.IsLive;
            IsRecording = stats.State == EngineState.Recording;
            CanSave = stats.State is EngineState.Buffering or EngineState.Recording;

            _statsTargetPercent = stats.BufferTargetSeconds > 0
                ? Math.Clamp(stats.BufferedSeconds / stats.BufferTargetSeconds * 100, 0, 100)
                : 0;

            ResolutionText = stats.Width > 0 ? $"{stats.Width}×{stats.Height}" : Loc.T("Common.Unknown");
            CaptureFpsText = stats.CaptureFps > 0 ? stats.CaptureFps.ToString("0.0") : Loc.T("Common.Unknown");
            DropText = stats.DropFrames.ToString();
            CpuText = $"{stats.CpuPercent:0.0} %";
            GpuText = stats.GpuEncodePercent >= 0 ? $"{stats.GpuEncodePercent:0} %" : Loc.T("Common.NotAvailable");
            BitrateText = stats.BitrateMbps > 0.1 ? $"{stats.BitrateMbps:0} Mbps" : $"{_settings.EffectiveBitrateMbps} Mbps";
            MemoryText = $"{stats.BufferedMegabytes:0} MB";
            DesktopAudioText = stats.DesktopAudioActive ? Loc.T("Common.On") : Loc.T("Common.Off");
            MicText = stats.MicrophoneActive ? Loc.T("Common.On") : Loc.T("Common.Off");

            BufferedOfText = Loc.F("Hero.Of",
                stats.BufferedSeconds.ToString("0.0"), stats.BufferTargetSeconds.ToString("0"));

            _micLevelTarget = stats.MicrophoneActive ? Math.Clamp(stats.MicrophoneLevel * 1.6, 0, 1) : 0;
        }

        if (_engine is not null)
        {
            var encoder = ResolveEncoder();
            if (encoder is not null)
            {
                EncoderBadge = $"{encoder.DisplayName} · {_settings.EffectiveFps}fps";
            }
        }

        PersistSettingsIfChanged();
        LogText = BuildLogTail();
        SaveHotkeyLabel = _settings.HotkeySaveReplay;
        RecordButtonText = IsRecording ? Loc.T("Action.RecordStop") : Loc.T("Action.RecordStart");
        BufferButtonText = IsLive ? Loc.T("Action.BufferStop") : Loc.T("Action.BufferStart");
        Raise(nameof(ToastVisible));
        Raise(nameof(NavStateBrush));
    }

    private static (string Label, string Short, string Detail, Brush Brush) Describe(ReplayStats stats)
    {
        return stats.State switch
        {
            EngineState.Buffering => (Loc.T("State.Buffering"), Loc.T("State.Buffering"),
                Loc.F("State.BufferingDetail", stats.BufferTargetSeconds.ToString("0")), ReadyBrush),
            EngineState.Recording => (Loc.T("State.Recording"), Loc.T("State.Recording"),
                Loc.F("State.RecordingDetail", stats.RecordingSeconds.ToString("0")), RecordBrush),
            EngineState.Saving => (Loc.T("State.Saving"), Loc.T("State.Saving"),
                Loc.T("State.SavingDetail"), SaveBrush),
            EngineState.Error => (Loc.T("State.Error"), Loc.T("State.Error"),
                Loc.T("State.ErrorDetail"), ErrorBrush),
            _ => (Loc.T("State.Stopped"), Loc.T("State.Stopped"), Loc.T("State.StoppedDetail"), IdleBrush)
        };
    }

    private void RefreshBufferTexts()
    {
        var seconds = _settings.EffectiveBufferSeconds;
        BufferRangeLabel = Loc.F("Hero.SaveLast", seconds);
        BufferExplain = Loc.F("Hero.Explain", seconds);
        MemoryEstimateText = Loc.F("Hero.Memory", _settings.EstimatedBufferMegabytes.ToString("0"));
        Raise(nameof(OutputDirectoryIsDefault));
        Raise(nameof(MicrophoneVolumeText));
        Raise(nameof(MicrophoneDelayText));
        Raise(nameof(BufferSecondsLabel));
    }

    private string BuildLogTail()
    {
        try
        {
            var entries = Log.Snapshot();
            var lines = entries.Skip(Math.Max(0, entries.Count - 120))
                .Select(e => $"{e.Timestamp:HH:mm:ss} [{e.Level,-5}] {e.Message}");
            return string.Join(Environment.NewLine, lines);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    // ---------------------------------------------------------------- shell helpers

    public void Notify(string message) => ShowToast(message);

    public void SetHotkeyWarning(string message)
    {
        if (string.Equals(HotkeyWarning, message, StringComparison.Ordinal))
        {
            return;
        }

        HotkeyWarning = message;
        Raise(nameof(HasHotkeyWarning));
    }

    public void RefreshSettingsBindings()
    {
        Raise(nameof(Settings));
        Raise(nameof(SaveHotkeyLabel));
        Raise(nameof(DesktopAudioEnabled));
        Raise(nameof(MicrophoneEnabled));
        Raise(nameof(MicrophoneVolume));
        Raise(nameof(MicrophoneVolumeText));
        Raise(nameof(MicrophoneDelayText));
        Raise(nameof(OutputDirectoryEdit));
        Raise(nameof(OutputDirectoryIsDefault));
        RaiseQualityPills();
    }

    private string _resolvedOutputDir = string.Empty;

    public void RefreshOutputPath()
    {
        _resolvedOutputDir = _settings.ResolveOutputDirectory();
        OutputDirectoryDisplay = _resolvedOutputDir;
        Raise(nameof(OutputDirectoryIsOverridden));
        Raise(nameof(OutputFallbackNote));

        if (OutputDirectoryIsOverridden && _resolvedOutputDir.Length > 0)
        {
            Log.Warn($"保存先を {_resolvedOutputDir} に変更しました (指定フォルダに書き込めません)");
        }
    }

    /// <summary>
    /// True when the folder the user picked could not be used and clips are going somewhere else.
    /// Silently writing to a different folder is exactly the bug this exists to prevent.
    /// </summary>
    public bool OutputDirectoryIsOverridden
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_settings.OutputDirectory) || _resolvedOutputDir.Length == 0)
            {
                return false;
            }

            try
            {
                var wanted = Path.GetFullPath(_settings.OutputDirectory).TrimEnd('\\');
                var actual = Path.GetFullPath(_resolvedOutputDir).TrimEnd('\\');
                return !string.Equals(wanted, actual, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public string OutputFallbackNote => Loc.F("Settings.OutputNotWritable", _resolvedOutputDir);

    public bool UseAlternativeHotkeys()
    {
        _settings.HotkeySaveReplay = "Ctrl+Alt+F10";
        _settings.HotkeyToggleRecord = "Ctrl+Alt+F9";
        _settings.HotkeyToggleBuffer = "Ctrl+Alt+F8";
        SettingsStore.Save(_settings);
        _lastSavedJson = SerializeSettings();
        Raise(nameof(SaveHotkeyLabel));
        Raise(nameof(Settings));
        return true;
    }

    private void ShowToast(string message)
    {
        ToastText = message;
        _toastUntil = DateTime.Now.AddSeconds(6);
        Raise(nameof(ToastVisible));
    }

    

    private void ReportPathFallback(string path)
    {
        try
        {
            Clipboard.SetText(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"クリップボードにコピーできません: {ex.Message}");
        }

        ShowToast(Loc.T("Toast.FolderFailed"));
    }

    public void OpenOutputFolder() => _ = OpenOutputFolderAsync();

    public void RevealClip(string path) => _ = RevealClipAsync(path);

    public async Task OpenOutputFolderAsync() => await OpenPathAsync(_settings.ResolveOutputDirectory()).ConfigureAwait(true);

    public Task OpenPathAsync(string dir)
    {
        if (!ShellLauncher.TryOpenFolder(dir))
        {
            ReportPathFallback(dir);
        }

        return Task.CompletedTask;
    }

    public Task RevealClipAsync(string path)
    {
        // COM cannot select an item inside Explorer, so open the containing folder - the clip is
        // the newest file there. This avoids spawning explorer.exe, which fails outright in
        // restricted sessions (0xc0000142).
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && ShellLauncher.TryOpenFolder(directory))
        {
            return Task.CompletedTask;
        }

        ReportPathFallback(path);
        return Task.CompletedTask;
    }

    public Task OpenClipAsync(string path)
    {
        if (ShellLauncher.TryOpenFile(path))
        {
            return Task.CompletedTask;
        }

        return RevealClipAsync(path);
    }

    public void DeleteClip(ClipEntry clip)
    {
        try
        {
            if (File.Exists(clip.Path))
            {
                File.Delete(clip.Path);
            }
        }
        catch (Exception ex)
        {
            ShowToast($"{ex.Message}");
            return;
        }

        _library.Remove(clip.Path);
        RefreshLibrary();
        ShowToast(Loc.T("Toast.Deleted"));
    }
}
