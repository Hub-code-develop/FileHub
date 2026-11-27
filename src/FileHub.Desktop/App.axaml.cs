using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using FileHub.Desktop.Views;

namespace FileHub.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Apply the saved theme before the first window is realized, otherwise a dark-theme user
        // sees a light flash on every launch.
        SetTheme(FileHub.Desktop.Services.Preferences.Current.ThemeMode);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Switch between system / light / dark themes.</summary>
    public void SetTheme(string mode)
    {
        RequestedThemeVariant = mode switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }

    /// <summary>
    /// Rebuild the main window so x:Static localized strings are re-evaluated.
    /// ShellViewModel is a singleton, so every open tab survives the rebuild.
    /// </summary>
    public void RestartMainWindow()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var previous = desktop.MainWindow;
            desktop.MainWindow = new MainWindow();
            desktop.MainWindow.Show();
            previous?.Close();
        }
    }
}
