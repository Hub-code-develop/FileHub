using Avalonia;
using Avalonia.Controls;

namespace FileHub.Desktop.Views;

/// <summary>
/// One preference row: a title, an optional explanatory hint and the editing control on the
/// right. The template lives in SettingsDialog.axaml; keeping the row a real control means the
/// dialog's XAML reads like a settings list instead of forty hand-built grids.
/// </summary>
public sealed class SettingRow : ContentControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<SettingRow, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<string> HintProperty =
        AvaloniaProperty.Register<SettingRow, string>(nameof(Hint), string.Empty);

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Hint
    {
        get => GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }
}
