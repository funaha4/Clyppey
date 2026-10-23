using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace Clyppey.App.Platform;

/// <summary>Parsed global hotkey.</summary>
public sealed record HotkeyGesture(uint Modifiers, uint VirtualKey, string Normalized)
{
    public static bool TryParse(string? text, out HotkeyGesture gesture, out string error)
    {
        gesture = new HotkeyGesture(0, 0, string.Empty);
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "ショートカットが空です";
            return false;
        }

        uint modifiers = 0;
        Key key = Key.None;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToArray();

        if (parts.Length == 0)
        {
            error = "ショートカットを解釈できません";
            return false;
        }

        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "alt":
                    modifiers |= HotkeyManager.ModAlt;
                    break;
                case "ctrl":
                case "control":
                    modifiers |= HotkeyManager.ModControl;
                    break;
                case "shift":
                    modifiers |= HotkeyManager.ModShift;
                    break;
                case "win":
                case "windows":
                    modifiers |= HotkeyManager.ModWin;
                    break;
                default:
                    if (!Enum.TryParse<Key>(part, true, out var parsed) || parsed == Key.None)
                    {
                        error = $"キー '{part}' を解釈できません";
                        return false;
                    }

                    key = parsed;
                    break;
            }
        }

        if (key == Key.None)
        {
            error = "修飾キーだけでは登録できません (例: Alt+F10)";
            return false;
        }

        if (modifiers == 0)
        {
            error = "Alt / Ctrl / Shift / Win のいずれかを組み合わせてください";
            return false;
        }

        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        gesture = new HotkeyGesture(modifiers, vk, Normalize(modifiers, key));
        return true;
    }

    public static string Normalize(uint modifiers, Key key)
    {
        var parts = new List<string>();
        if ((modifiers & HotkeyManager.ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & HotkeyManager.ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & HotkeyManager.ModShift) != 0) parts.Add("Shift");
        if ((modifiers & HotkeyManager.ModWin) != 0) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }
}

/// <summary>
/// System-wide hotkeys via a message-only window and RegisterHotKey, so the shortcuts work
/// while a game has focus - which is the whole point of an instant replay key.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private const int WmHotkey = 0x0312;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private readonly Dictionary<int, string> _labels = new();
    private int _nextId = 0x4310;
    private bool _disposed;

    public HotkeyManager()
    {
        var parameters = new HwndSourceParameters("ClyppeyHotkeyWindow")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
            ParentWindow = HwndMessage
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    /// <summary>Keys that could not be registered (usually because another app owns them).</summary>
    public List<string> Failures { get; } = new();

    public bool Register(string? gestureText, Action action, out string? error)
    {
        error = null;
        if (!HotkeyGesture.TryParse(gestureText, out var gesture, out var parseError))
        {
            error = parseError;
            return false;
        }

        var id = _nextId++;
        if (!RegisterHotKey(_source.Handle, id, gesture.Modifiers | ModNoRepeat, gesture.VirtualKey))
        {
            var code = Marshal.GetLastWin32Error();
            error = code == 1409
                ? $"{gesture.Normalized} は他のアプリが使用中です"
                : $"登録に失敗しました (code {code})";
            return false;
        }

        _actions[id] = action;
        _labels[id] = gesture.Normalized;
        return true;
    }

    /// <summary>
    /// Tries each candidate in order and uses the first one that registers. This is what lets
    /// Clyppey keep working on machines where another overlay already owns the default keys.
    /// </summary>
    public bool RegisterAny(IEnumerable<string> candidates, Action action, out string used, out List<string> errors)
    {
        used = string.Empty;
        errors = new List<string>();

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (Register(candidate, action, out var error))
            {
                used = HotkeyGesture.TryParse(candidate, out var gesture, out _)
                    ? gesture.Normalized
                    : candidate.Trim();
                return true;
            }

            errors.Add($"{candidate.Trim()} ({error})");
        }

        Failures.Add($"{string.Join(" / ", errors)}");
        return false;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys.ToList())
        {
            try
            {
                UnregisterHotKey(_source.Handle, id);
            }
            catch (Exception)
            {
                // Ignore.
            }
        }

        _actions.Clear();
        _labels.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey)
        {
            return IntPtr.Zero;
        }

        var id = wParam.ToInt32();
        if (_actions.TryGetValue(id, out var action))
        {
            handled = true;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Core.Log.Error("ホットキー処理でエラー", ex);
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
