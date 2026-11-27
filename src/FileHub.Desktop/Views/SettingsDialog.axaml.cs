using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FileHub.Desktop.ViewModels;

namespace FileHub.Desktop.Views;

/// <summary>
/// Modal settings window. It is opened from the gear button in the tab strip and from the sidebar
/// footer, and it also hosts the extension manager — that is why Extensions is no longer a
/// workspace page of its own.
/// </summary>
public partial class SettingsDialog : Window
{
    private static SettingsDialog? _open;
    private readonly SettingsViewModel _settings;

    public SettingsDialog() : this(ShellViewModel.Current)
    {
    }

    public SettingsDialog(ShellViewModel shell)
    {
        InitializeComponent();
        _settings = new SettingsViewModel(shell);
        DataContext = _settings;

        // No page to close and the rail already names the section, so drop the extension
        // manager's own header controls.
        ExtensionsHost.IsEmbedded = true;
        Closed += (_, _) => { if (ReferenceEquals(_open, this)) _open = null; };

        // Esc closes a modal preference window; without this it would only close via the button.
        KeyDown += (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Escape) return;
            e.Handled = true;
            Close();
        };
    }

    /// <summary>Shows the dialog, or brings the already-open one to the front.</summary>
    public static async Task ShowAsync(ShellViewModel shell)
    {
        if (_open is not null)
        {
            _open.Activate();
            return;
        }

        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow;
        var dialog = new SettingsDialog(shell);
        _open = dialog;

        if (owner is null || ReferenceEquals(owner, dialog))
        {
            dialog.Show();
            return;
        }

        await dialog.ShowDialog(owner).ConfigureAwait(true);
    }

    private void OnSectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Named panels rather than a template selector: one panel per rail entry, in rail order.
        if (GeneralSection is null) return;
        var index = Nav.SelectedIndex;
        GeneralSection.IsVisible = index == 0;
        FilesSection.IsVisible = index == 1;
        CodeSection.IsVisible = index == 2;
        OfficeSection.IsVisible = index == 3;
        ExtensionsSection.IsVisible = index == 4;
        AboutSection.IsVisible = index == 5;

        // The extension detail pane is empty until something is selected; pick the first item so
        // the section never opens as a blank panel.
        if (index == 4 && _settings.Extensions.Selected is null &&
            _settings.Extensions.Items.Count > 0)
        {
            _settings.Extensions.Selected = _settings.Extensions.Items[0];
        }
    }

    private void OnChangeLanguage(object? sender, RoutedEventArgs e)
    {
        // A language switch rebuilds the main window; a modal child of it must be gone first.
        Close();
        Dispatcher.UIThread.Post(() => _settings.CycleLanguage(),
            DispatcherPriority.Background);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
