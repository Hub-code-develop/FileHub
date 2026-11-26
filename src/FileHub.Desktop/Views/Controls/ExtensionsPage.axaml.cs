using Avalonia.Controls;
using Avalonia.Input;
using FileHub.Desktop.ViewModels;

namespace FileHub.Desktop.Views.Controls;

public partial class ExtensionsPage : UserControl
{
    public ExtensionsPage()
    {
        InitializeComponent();
        DataContext = ShellViewModel.Current.Extensions;
    }

    private void OnMarketplaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (DataContext is ExtensionsViewModel vm && !vm.IsBusy)
            vm.SearchMarketplaceCommand.Execute(null);
    }
}
