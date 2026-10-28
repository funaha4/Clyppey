using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Clyppey.App.ViewModels;
using Clyppey.Core;
using Clyppey.Core.Ffmpeg;
using Clyppey.Core.Storage;
using Microsoft.Win32;

namespace Clyppey.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        ApplyDarkFrame();
    }

    /// <summary>Keeps the (borderless) window rounded-looking on Windows 11 and dark elsewhere.</summary>
    private void ApplyDarkFrame()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var enabled = 1;
            if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
            }

            // Rounded corners where the OS supports it (Windows 11 22000+).
            var preference = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(handle, 33, ref preference, sizeof(int));
        }
        catch (Exception ex)
        {
            Log.Debug($"ウィンドウ装飾を設定できません: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- title bar

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove can throw if the button was released mid-gesture.
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => (Application.Current as App)?.HideToTray();

    private void CloseToTray_Click(object sender, RoutedEventArgs e) => (Application.Current as App)?.HideToTray();

    // ---------------------------------------------------------------- actions

    private async void SaveReplay_Click(object sender, RoutedEventArgs e) => await _viewModel.SaveReplayAsync();

    private async void ToggleRecord_Click(object sender, RoutedEventArgs e) => await _viewModel.ToggleRecordAsync();

    private async void ToggleBuffer_Click(object sender, RoutedEventArgs e) => await _viewModel.ToggleBufferAsync();

    private async void OpenFolder_Click(object sender, RoutedEventArgs e) => await _viewModel.OpenOutputFolderAsync();

    private void ToggleAdvanced_Click(object sender, RoutedEventArgs e) =>
        _viewModel.IsAdvancedOpen = !_viewModel.IsAdvancedOpen;

    private async void Redetect_Click(object sender, RoutedEventArgs e) => await _viewModel.RedetectAsync();

    private async void OpenClip_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ClipEntry clip)
        {
            await _viewModel.OpenClipAsync(clip.Path);
        }
    }

    private async void RevealClip_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ClipEntry clip)
        {
            await _viewModel.RevealClipAsync(clip.Path);
        }
    }

    private void DeleteClip_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ClipEntry clip)
        {
            _viewModel.DeleteClip(clip);
        }
    }

    private void TrimClip_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ClipEntry clip)
        {
            return;
        }

        var ffmpeg = FfmpegLocator.Find();
        if (ffmpeg is null)
        {
            return;
        }

        var editor = new ClipEditorWindow(
            clip,
            ffmpeg,
            (path, duration) => _viewModel.RegisterExternalClip(path, duration))
        {
            Owner = this
        };

        editor.ShowDialog();
    }

    private async void ClipList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox { SelectedItem: ClipEntry clip })
        {
            await _viewModel.OpenClipAsync(clip.Path);
        }
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select a folder" };
        try
        {
            var current = _viewModel.OutputDirectoryDisplay;
            if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
            {
                dialog.InitialDirectory = current;
            }
        }
        catch (Exception)
        {
            // Initial directory is optional.
        }

        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.OutputDirectoryEdit = dialog.FolderName;
        }
    }

    private async void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(AppPaths.LogFile))
            {
                await _viewModel.OpenClipAsync(AppPaths.LogFile);
            }
            else
            {
                await _viewModel.OpenOutputFolderAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"ログを開けません: {ex.Message}");
        }
    }

    private async void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.OpenPathAsync(AppPaths.AppDataRoot);
        }
        catch (Exception ex)
        {
            Log.Warn($"フォルダを開けません: {ex.Message}");
        }
    }

    private void LogBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box)
        {
            box.ScrollToEnd();
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
