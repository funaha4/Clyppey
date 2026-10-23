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

        if (HotkeySaveButton is not null)
        {
            HotkeySaveButton.Content = viewModel.Settings.HotkeySaveReplay;
        }

        if (HotkeyRecordButton is not null)
        {
            HotkeyRecordButton.Content = viewModel.Settings.HotkeyToggleRecord;
        }

        if (HotkeyBufferButton is not null)
        {
            HotkeyBufferButton.Content = viewModel.Settings.HotkeyToggleBuffer;
        }
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
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
        {
            return; // waiting for the non-modifier key
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

        var isFunctionKey = key is >= Key.F1 and <= Key.F24;
        if (parts.Count == 0 && !isFunctionKey)
        {
            // A bare letter would fire while typing anywhere; ask for a modifier.
            _capturingHotkeyButton.Content = Loc.T("Settings.PressKeys");
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
