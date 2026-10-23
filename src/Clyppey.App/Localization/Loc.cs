using System.Windows;

namespace Clyppey.App.Localization;

/// <summary>
/// Runtime language switching. Strings live in Resources/Strings.{en,ja}.xaml so XAML can use
/// {DynamicResource Key} and update the moment the language changes - no restart, no rebinding.
/// Code-behind text goes through <see cref="T"/> and <see cref="F"/>.
/// </summary>
public static class Loc
{
    public const string English = "en";
    public const string Japanese = "ja";

    private static ResourceDictionary? _current;

    public static string Current { get; private set; } = English;

    /// <summary>Raised after the language changed so view models can refresh their own text.</summary>
    public static event Action? Changed;

    public static void Initialize(string? language) => Set(language, notify: false);

    public static string Normalize(string? language) =>
        string.Equals(language, Japanese, StringComparison.OrdinalIgnoreCase) ? Japanese : English;

    public static void Set(string? language, bool notify = true)
    {
        var lang = Normalize(language);
        var app = Application.Current;
        if (app is null)
        {
            Current = lang;
            return;
        }

        var dictionary = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Resources/Strings.{lang}.xaml", UriKind.Absolute)
        };

        if (_current is not null)
        {
            app.Resources.MergedDictionaries.Remove(_current);
        }

        app.Resources.MergedDictionaries.Add(dictionary);
        _current = dictionary;
        Current = lang;

        if (notify)
        {
            Changed?.Invoke();
        }
    }

    public static string T(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;

    public static string F(string key, params object?[] args)
    {
        var format = T(key);
        try
        {
            return string.Format(format, args);
        }
        catch (FormatException)
        {
            return format;
        }
    }
}
