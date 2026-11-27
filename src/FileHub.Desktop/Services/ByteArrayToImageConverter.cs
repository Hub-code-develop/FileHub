using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace FileHub.Desktop.Services;

/// <summary>Displays an image stored in an Office document without writing a temporary file.</summary>
public sealed class ByteArrayToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] bytes || bytes.Length == 0) return null;
        try
        {
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => null;
}
