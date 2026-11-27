using System;
using System.Collections;
using System.Globalization;
using Avalonia.Data.Converters;

namespace FileHub.Desktop.Services;

/// <summary>Joins a row of pivot cells into one monospace line for the advanced panel.</summary>
public sealed class StringListConverter : IValueConverter
{
    public static readonly StringListConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string text) return text;
        if (value is IEnumerable items)
        {
            var parts = new System.Collections.Generic.List<string>();
            foreach (var item in items) parts.Add(item?.ToString() ?? string.Empty);
            return string.Join("  |  ", parts);
        }
        return string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
