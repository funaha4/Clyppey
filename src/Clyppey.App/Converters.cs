using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Clyppey.App.Localization;

namespace Clyppey.App;

/// <summary>Shows an element when the bound bool is FALSE (e.g. "no microphone found").</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}

/// <summary>Maps a clip's stored mode to the localised label.</summary>
public sealed class ClipModeTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as string) switch
        {
            "record" => Loc.T("Clips.ModeRecord"),
            "replay" => Loc.T("Clips.ModeReplay"),
            _ => value?.ToString() ?? string.Empty
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
