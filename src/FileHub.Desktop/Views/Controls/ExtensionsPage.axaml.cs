using Avalonia.Controls;
using Avalonia.Input;
using FileHub.Desktop.ViewModels;

namespace FileHub.Desktop.Views.Controls;

/// <summary>
/// VS Code-style extension manager. It is no longer a workspace page: the settings dialog hosts it
/// inside its "Extensions" section, so it has to work without its own page chrome.
/// </summary>
public partial class ExtensionsPage : UserControl
{
    private bool _isEmbedded;

    public ExtensionsPage()
    {
        InitializeComponent();
        DataContext = ShellViewModel.Current.Extensions;
    }

    /// <summary>
    /// Drops the page-level chrome (close button and heading) when the control is embedded in the
    /// settings dialog. The installed/marketplace switch stays, because it is the section's own
    /// navigation.
    /// </summary>
    public bool IsEmbedded
    {
        get => _isEmbedded;
        set
        {
            _isEmbedded = value;
            _closeButton.IsVisible = !value;
            _title.IsVisible = !value;
        }
    }

    private void OnMarketplaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (DataContext is ExtensionsViewModel vm && !vm.IsBusy)
            vm.SearchMarketplaceCommand.Execute(null);
    }
}
