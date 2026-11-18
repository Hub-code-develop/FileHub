using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FileHub.Desktop.Views.Controls;

/// <summary>
/// The Files App inner toolbar (command bar). Behaviour lives in the view model, so the
/// control itself only loads its XAML — the same split Files uses between
/// <c>Toolbar.xaml</c> and <c>Toolbar.xaml.cs</c>.
/// </summary>
public partial class FilesToolbar : UserControl
{
    public FilesToolbar() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
