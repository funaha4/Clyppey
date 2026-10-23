using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Clyppey.Core;

namespace Clyppey.App.Platform;

/// <summary>
/// Opens folders and files through the *running* shell instead of spawning explorer.exe.
///
/// Spawning a child explorer.exe is fragile: in restricted sessions it starts and then dies with
/// STATUS_DLL_INIT_FAILED (0xc0000142), which the caller never sees because Process.Start already
/// returned a PID. Asking the shell via COM has no child process at all, so it works in both
/// normal desktops and locked-down ones.
/// </summary>
public static class ShellLauncher
{
    /// <summary>Opens a folder in Explorer. Returns false only when nothing could be attempted.</summary>
    public static bool TryOpenFolder(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        try
        {
            if (!Directory.Exists(directory))
            {
                Log.Warn($"フォルダが存在しません: {directory}");
                return false;
            }
        }
        catch (Exception)
        {
            return false;
        }

        if (TryShellExplore(directory))
        {
            return true;
        }

        return TrySpawnExplorer($"\"{directory}\"");
    }

    /// <summary>Opens a file with its default application through the shell.</summary>
    public static bool TryOpenFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return TryShellExecute(path) || TryShellProcessStart(path);
    }

    private static bool TryShellExplore(string directory)
    {
        object? shell = null;
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null)
            {
                return false;
            }

            shell = Activator.CreateInstance(type);
            if (shell is null)
            {
                return false;
            }

            dynamic api = shell;
            api.Explore(directory);
            Log.Info($"エクスプローラーで開きました: {directory}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"シェル経由でフォルダを開けませんでした: {ex.Message}");
            return false;
        }
        finally
        {
            Release(shell);
        }
    }

    private static bool TryShellExecute(string path)
    {
        object? shell = null;
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null)
            {
                return false;
            }

            shell = Activator.CreateInstance(type);
            if (shell is null)
            {
                return false;
            }

            dynamic api = shell;
            api.ShellExecute(path, string.Empty, string.Empty, "open", 1);
            Log.Info($"シェル経由で開きました: {path}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"シェル経由でファイルを開けませんでした: {ex.Message}");
            return false;
        }
        finally
        {
            Release(shell);
        }
    }

    private static bool TrySpawnExplorer(string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"explorer.exe を起動できません: {ex.Message}");
            return false;
        }
    }

    private static bool TryShellProcessStart(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"関連付けアプリで開けません: {ex.Message}");
            return false;
        }
    }

    private static void Release(object? comObject)
    {
        if (comObject is null)
        {
            return;
        }

        try
        {
            if (Marshal.IsComObject(comObject))
            {
                Marshal.FinalReleaseComObject(comObject);
            }
        }
        catch (Exception)
        {
            // Ignore.
        }
    }
}
