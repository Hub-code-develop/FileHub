using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace FileHub.Desktop.Services;

/// <summary>Converts an RRGGBB cell fill string to a solid brush.</summary>
public sealed class StringToBrushConverter : IValueConverter
{
    public static readonly StringToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hex = value?.ToString();
        if (string.IsNullOrWhiteSpace(hex) || hex.Length != 6) return Brushes.Transparent;
        try
        {
            return new SolidColorBrush(Color.Parse("#" + hex));
        }
        catch
        {
            return Brushes.Transparent;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => null;
}

/// <summary>Maps a boolean to FontStyle weights used by the slide canvas.</summary>
public sealed class BoolToFontWeightConverter : IValueConverter
{
    public static readonly BoolToFontWeightConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? FontWeight.Bold : FontWeight.Normal;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => null;
}

/// <summary>
/// Turns the "sheet grid lines" preference into a cell border. Kept as a converter so the
/// worksheet template can stay declarative and the toggle re-renders every cell at once.
/// </summary>
public sealed class BoolToGridThicknessConverter : IValueConverter
{
    public static readonly BoolToGridThicknessConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is false ? default(Thickness) : new Thickness(0, 0, 1, 1);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => null;
}
