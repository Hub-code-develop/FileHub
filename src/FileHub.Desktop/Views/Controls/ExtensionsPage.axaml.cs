using Avalonia.Controls;
using FileHub.Desktop.ViewModels;

namespace FileHub.Desktop.Views.Controls;

public partial class ExtensionsPage : UserControl
{
    public ExtensionsPage()
    {
        InitializeComponent();
        DataContext = ShellViewModel.Current.Extensions;
    }
}
