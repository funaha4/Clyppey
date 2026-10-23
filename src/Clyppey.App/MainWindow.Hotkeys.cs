using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Clyppey.App.Localization;
using Clyppey.App.Platform;
using Clyppey.App.ViewModels;

namespace Clyppey.App;

/// <summary>
/// Hotkey capture: press a button, then press the combination, and it is stored.
/// Typing "Ctrl+Alt+F10" by hand was the least friendly part of the settings page.
/// </summary>
public partial class MainWindow
{
    private Button? _capturingHotkeyButton;
    private string? _capturingHotkeyName;
    private System.Windows.Threading.DispatcherTimer? _captureTimeout;

    private void SettingsView_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshHotkeyButtons();
        PreviewKeyDown -= HotkeyCapture_PreviewKeyDown;
        PreviewKeyDown += HotkeyCapture_PreviewKeyDown;
        Deactivated -= MainWindow_Deactivated;
        Deactivated += MainWindow_Deactivated;
    }

    /// <summary>Never leave the global hotkeys disabled when the user walks away mid-capture.</summary>
    private void MainWindow_Deactivated(object? sender, EventArgs e)
    {
        if (_capturingHotkeyButton is not null)
        {
            FinishHotkeyCapture(null);
        }
    }

    private void RefreshHotkeyButtons()
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var unset = Loc.T("Settings.HotkeyUnset");
        if (HotkeySaveButton is not null)
        {
            HotkeySaveButton.Content = Display(viewModel.Settings.HotkeySaveReplay, unset);
        }

        if (HotkeyRecordButton is not null)
        {
            HotkeyRecordButton.Content = Display(viewModel.Settings.HotkeyToggleRecord, unset);
        }

        if (HotkeyBufferButton is not null)
        {
            HotkeyBufferButton.Content = Display(viewModel.Settings.HotkeyToggleBuffer, unset);
        }
    }

    private static string Display(string? combination, string unset) =>
        string.IsNullOrWhiteSpace(combination) ? unset : combination;

    /// <summary>Removes a binding entirely.</summary>
    private void ClearHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        switch (button.Tag as string)
        {
            case "Save":
                viewModel.Settings.HotkeySaveReplay = string.Empty;
                break;
            case "Record":
                viewModel.Settings.HotkeyToggleRecord = string.Empty;
                break;
            case "Buffer":
                viewModel.Settings.HotkeyToggleBuffer = string.Empty;
                break;
        }

        (Application.Current as App)?.ReRegisterHotkeys();
        RefreshHotkeyButtons();
    }

    private void HotkeyCapture_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        _capturingHotkeyButton = button;
        _capturingHotkeyName = button.Tag as string;

        // The global hotkey would otherwise eat this very combination.
        (Application.Current as App)?.SuspendHotkeys();

        _captureTimeout?.Stop();
        _captureTimeout = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(9)
        };
        _captureTimeout.Tick += (_, _) => FinishHotkeyCapture(null);
        _captureTimeout.Start();
        button.Content = Loc.T("Settings.PressKeys");
        button.Focus();
        Keyboard.Focus(button);
    }

    private void HotkeyCapture_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturingHotkeyButton is null || _capturingHotkeyName is null)
        {
            return;
        }

        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return; // waiting for the non-modifier key
        }

        if (key == Key.None)
        {
            // Keys produced by Fn / vendor drivers often arrive with no usable virtual key,
            // so they cannot be registered. Say so instead of looking stuck.
            _capturingHotkeyButton.Content = Loc.T("Settings.KeyUnsupported");
            return;
        }

        if (key == Key.Escape)
        {
            FinishHotkeyCapture(null);
            return;
        }

        var parts = new List<string>();
        var modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & ModifierKeys.Alt) != 0)
        {
            parts.Add("Alt");
        }

        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifiers & ModifierKeys.Windows) != 0)
        {
            parts.Add("Win");
        }

        // Shift alone is just typing (Shift+Space, Shift+A...): require Ctrl, Alt or Win,
        // and never let a navigation key become a global shortcut on its own.
        var hasStrongModifier = (modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0;
        var isNavigationKey = key is Key.Space or Key.Enter or Key.Tab or Key.Back or Key.Delete;
        if ((!hasStrongModifier && !(key is >= Key.F1 and <= Key.F24)) || (isNavigationKey && !hasStrongModifier))
        {
            // A bare letter would fire while typing anywhere; ask for a modifier.
            _capturingHotkeyButton.Content = Loc.T("Settings.NeedModifier");
            return;
        }

        parts.Add(key.ToString());
        var combination = string.Join("+", parts);

        if (!HotkeyGesture.TryParse(combination, out _, out var error))
        {
            _capturingHotkeyButton.Content = error;
            return;
        }

        FinishHotkeyCapture(combination);
    }

    private void FinishHotkeyCapture(string? combination)
    {
        var button = _capturingHotkeyButton;
        var name = _capturingHotkeyName;
        _capturingHotkeyButton = null;
        _capturingHotkeyName = null;
        _captureTimeout?.Stop();
        _captureTimeout = null;

        if (button is null || DataContext is not MainViewModel viewModel)
        {
            RefreshHotkeyButtons();
            return;
        }

        if (combination is not null)
        {
            switch (name)
            {
                case "Save":
                    viewModel.Settings.HotkeySaveReplay = combination;
                    break;
                case "Record":
                    viewModel.Settings.HotkeyToggleRecord = combination;
                    break;
                case "Buffer":
                    viewModel.Settings.HotkeyToggleBuffer = combination;
                    break;
            }

        }

        // Always restore the global hotkeys, even when the capture was cancelled.
        (Application.Current as App)?.ReRegisterHotkeys();
        RefreshHotkeyButtons();
    }
}
