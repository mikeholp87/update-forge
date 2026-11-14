using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace UpdateForge.Desktop.Converters;

/// <summary>Maps a row's status text to the dot colour: amber = an update is waiting, red = failed, green = current, grey = nothing to say.</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public static readonly StatusBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = (value as string) switch
        {
            "Update ready" or "Approval required" or "Manual update" or "Pinned" => "Warn",
            "Failed" => "Bad",
            "Up to date" => "Ok",
            _ => "Muted",
        };
        var app = Avalonia.Application.Current;
        return app is not null && app.TryGetResource(key, app.ActualThemeVariant, out var brush) && brush is IBrush found ? found : Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
