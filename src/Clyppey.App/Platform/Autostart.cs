using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Clyppey.App.Platform;

/// <summary>Start-with-Windows via the per-user Run key (no elevation, easily reversible).</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Clyppey";

    public static string ExecutablePath
    {
        get
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                return path;
            }

            return Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception ex)
        {
            Core.Log.Warn($"自動起動の確認に失敗: {ex.Message}");
            return false;
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                var exe = ExecutablePath;
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                {
                    return false;
                }

                key.SetValue(ValueName, $"\"{exe}\" --minimized");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex)
        {
            Core.Log.Warn($"自動起動の設定に失敗: {ex.Message}");
            return false;
        }
    }
}
