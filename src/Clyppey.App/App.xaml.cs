using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clyppey.App.Localization;
using Clyppey.App.Platform;
using Clyppey.App.ViewModels;
using Clyppey.Core;
using Clyppey.Core.Settings;
using Clyppey.Core.Storage;

namespace Clyppey.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private TrayIconHost? _tray;
    private HotkeyManager? _hotkeys;
    private MainWindow? _window;
    private bool _quitting;

    public static MainViewModel? ViewModel { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var screenshotPath = GetArgumentValue(e.Args, "--screenshot");
        var tabIndex = int.TryParse(GetArgumentValue(e.Args, "--tab"), out var parsedTab) ? parsedTab : -1;
        // dev only: scroll the settings page so lower sections can be captured too
        var scrollOffset = int.TryParse(GetArgumentValue(e.Args, "--scroll"), out var parsedScroll) ? parsedScroll : -1;

        if (screenshotPath is null)
        {
            _singleInstance = new Mutex(true, "Clyppey.SingleInstance", out var createdNew);
            if (!createdNew)
            {
                MessageBox.Show("Clyppey is already running — look for the tray icon.",
                    "Clyppey", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
        }

        AppPaths.EnsureDirectories();
        // dev only: exercise the shell launcher end to end (no window, no tray)
        var shellTest = GetArgumentValue(e.Args, "--test-shell");
        if (shellTest is not null)
        {
            var ok = System.IO.Directory.Exists(shellTest)
                ? ShellLauncher.TryOpenFolder(shellTest)
                : ShellLauncher.TryOpenFile(shellTest);
            Log.Info($"shell test: {shellTest} -> {ok}");
            Console.Error.WriteLine($"shell test: {shellTest} -> {ok}");
            Shutdown(ok ? 0 : 1);
            return;
        }

        // Settings may live in a different folder than last time (permissions change, the app was
        // run from a different shell...). Recover them instead of silently starting from defaults.
        AppPaths.MigrateSettingsIfNeeded();

        // Remove ring buffers left behind by a crash or a forced shutdown.
        Clyppey.Core.Mp4.DiskFragmentStore.RemoveStale();

        var settings = SettingsStore.Load();
        Loc.Initialize(settings.Language);

        if (e.Args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase)))
        {
            settings.StartMinimized = true;
        }

        Log.Info($"起動: {AppPaths.AppDataRoot} (fallback={AppPaths.IsFallbackRoot}) lang={Loc.Current}");

        var library = new ClipLibrary();
        library.Load();

        ViewModel = new MainViewModel(settings, library);
        _window = new MainWindow(ViewModel);

        if (screenshotPath is not null)
        {
            _window.Show();
            if (tabIndex >= 0)
            {
                _window.Nav.SelectedIndex = tabIndex;
            }

            // dev only: exercise the live language switch exactly like the header pills do
            var languageOverride = GetArgumentValue(e.Args, "--lang");
            if (!string.IsNullOrWhiteSpace(languageOverride))
            {
                ViewModel.SetLanguage(languageOverride);
            }

            if (scrollOffset > 0)
            {
                _window.SettingsScroll.ScrollToVerticalOffset(scrollOffset);
            }

            _ = ViewModel.InitializeAsync();
            ReRegisterHotkeys();
            CaptureAndQuit(screenshotPath);
            return;
        }

        if (!settings.StartMinimized)
        {
            ShowWindow();
        }

        // Alt+F4 and the taskbar close button behave like the title bar's ✕: hide to the tray
        // and keep recording. Only Quit() really ends the process.
        _window.Closing += (_, args) =>
        {
            if (_quitting)
            {
                return;
            }

            args.Cancel = true;
            HideToTray();
        };

        SetupTray();
        ReRegisterHotkeys();
        _ = ViewModel.InitializeAsync();
    }

    // ---------------------------------------------------------------- window

    public void ShowWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
    }

    /// <summary>Hides to the tray: the replay buffer keeps running, which is the whole point.</summary>
    public void HideToTray()
    {
        _window?.Hide();
        _tray?.ShowBalloon("Clyppey", Loc.T("Header.Close"));
    }

    // ---------------------------------------------------------------- tray & hotkeys

    private void SetupTray()
    {
        _tray = new TrayIconHost(
            show: ShowWindow,
            save: () => _ = ViewModel?.SaveReplayAsync(),
            toggleRecord: () => _ = ViewModel?.ToggleRecordAsync(),
            toggleBuffer: () => _ = ViewModel?.ToggleBufferAsync(),
            openFolder: () => ViewModel?.OpenOutputFolder(),
            quit: Quit);
    }

    /// <summary>
    /// Releases the global hotkeys so the settings page can see the keys the user presses.
    /// A registered hotkey swallows its combination before WPF ever sees it.
    /// </summary>
    public void SuspendHotkeys()
    {
        try
        {
            _hotkeys?.UnregisterAll();
        }
        catch (Exception ex)
        {
            Log.Debug($"ホットキーを一時解除できません: {ex.Message}");
        }
    }

    public void ReRegisterHotkeys()
    {
        _hotkeys ??= new HotkeyManager();
        _hotkeys.UnregisterAll();
        _hotkeys.Failures.Clear();

        if (ViewModel is null)
        {
            return;
        }

        var settings = ViewModel.Settings;
        var remapped = new List<string>();
        var failed = new List<string>();

        RegisterWithFallback(settings.HotkeySaveReplay,
            new[] { "Ctrl+Alt+F10", "Ctrl+Shift+F10", "Alt+Shift+F10", "Ctrl+Alt+Shift+F10" },
            Loc.T("Settings.HotkeySave"), () => _ = ViewModel.SaveReplayAsync(),
            value => settings.HotkeySaveReplay = value, remapped, failed);

        RegisterWithFallback(settings.HotkeyToggleRecord,
            new[] { "Ctrl+Alt+F9", "Ctrl+Shift+F9", "Alt+Shift+F9", "Ctrl+Alt+Shift+F9" },
            Loc.T("Settings.HotkeyRecord"), () => _ = ViewModel.ToggleRecordAsync(),
            value => settings.HotkeyToggleRecord = value, remapped, failed);

        RegisterWithFallback(settings.HotkeyToggleBuffer,
            new[] { "Ctrl+Alt+F8", "Ctrl+Shift+F8", "Alt+Shift+F8", "Ctrl+Alt+Shift+F8" },
            Loc.T("Settings.HotkeyBuffer"), () => _ = ViewModel.ToggleBufferAsync(),
            value => settings.HotkeyToggleBuffer = value, remapped, failed);

        if (remapped.Count > 0 || failed.Count > 0)
        {
            SettingsStore.Save(settings);
            ViewModel.RefreshSettingsBindings();
        }

        if (remapped.Count > 0)
        {
            ViewModel.Notify(Loc.F("Toast.HotkeyRemapped", string.Join(", ", remapped)));
        }

        if (failed.Count > 0)
        {
            Log.Warn("ホットキーを登録できませんでした: " + string.Join(", ", failed));
            ViewModel.SetHotkeyWarning(Loc.F("Toast.HotkeyFailed", string.Join(", ", failed)));
        }
        else
        {
            ViewModel.SetHotkeyWarning(string.Empty);
        }
    }

    private void RegisterWithFallback(
        string configured, string[] alternatives, string label, Action action,
        Action<string> apply, List<string> remapped, List<string> failed)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return; // the user removed this binding on purpose
        }

        var candidates = new List<string> { configured.Trim() };

        // Only the built-in defaults may fall back to another key. A combination the user picked
        // must never be silently replaced - if it is taken, say so and leave it as chosen.
        if (alternatives.Any(a => string.Equals(a, configured.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            candidates.AddRange(alternatives.Where(a => !string.Equals(a, configured.Trim(), StringComparison.OrdinalIgnoreCase)));
        }

        if (_hotkeys!.RegisterAny(candidates, action, out var used, out var errors))
        {
            if (!string.Equals(used, configured.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                apply(used);
                remapped.Add($"{label}: {used}");
            }

            return;
        }

        failed.Add($"{label} ({errors.FirstOrDefault() ?? "?"})");
    }

    // ---------------------------------------------------------------- shutdown

    public void Quit()
    {
        _quitting = true;
        ViewModel?.Shutdown();
        _tray?.Dispose();
        _hotkeys?.Dispose();
        _window?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            ViewModel?.Shutdown();
        }
        catch (Exception)
        {
            // Ignore.
        }

        _tray?.Dispose();
        _hotkeys?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    // ---------------------------------------------------------------- dev: UI snapshot

    private void CaptureAndQuit(string path)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                CaptureWindow(path);
            }
            catch (Exception ex)
            {
                Log.Error("スクリーンショットの保存に失敗", ex);
            }

            _quitting = true;
            Shutdown();
        };
        timer.Start();
    }

    private void CaptureWindow(string path)
    {
        if (_window is null)
        {
            return;
        }

        _window.UpdateLayout();
        var width = (int)Math.Max(1, _window.ActualWidth);
        var height = (int)Math.Max(1, _window.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(_window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static string? GetArgumentValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
