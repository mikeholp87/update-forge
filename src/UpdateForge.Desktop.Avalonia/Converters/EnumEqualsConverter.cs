using System.Globalization;
using Avalonia.Data.Converters;

namespace UpdateForge.Desktop.Converters;

/// <summary>True when the bound value's text equals the converter parameter. Used to show one page at a time and to highlight the active nav button.</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public static readonly EnumEqualsConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
