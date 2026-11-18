namespace FileHub.Desktop.Views.Controls;

/// <summary>
/// Files App's Pane/InfoPane.xaml ported to Avalonia. DataContext is the active tab,
/// assigned by MainWindow.SyncActiveTab; the shell-level toggle stays in MainWindow.
/// </summary>
public partial class FilesInfoPane : Avalonia.Controls.UserControl
{
    public FilesInfoPane() => InitializeComponent();
}
