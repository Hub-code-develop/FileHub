using Avalonia.Controls;

namespace FileHub.Desktop.Views.Controls;

/// <summary>
/// Files App's StatusBar.xaml ported to Avalonia. The details-pane toggle is a shell
/// command, so the button is exposed to MainWindow rather than bound in XAML.
/// </summary>
public partial class FilesStatusBar : UserControl
{
    public FilesStatusBar() => InitializeComponent();

    public Button DetailsToggleButton => _detailsToggle;
}
