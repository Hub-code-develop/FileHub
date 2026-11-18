using Avalonia.Controls;
using FileHub.Desktop.Controls;

namespace FileHub.Desktop.Views.Controls;

/// <summary>
/// Files App's SidebarView.xaml ported to Avalonia. The window fills the two item hosts
/// and wires the footer item, matching how Files binds MenuItemsSource / Footer.
/// </summary>
public partial class FilesSidebar : UserControl
{
    public FilesSidebar() => InitializeComponent();

    public ItemsControl QuickItems => _quickItems;
    public ItemsControl DriveItems => _driveItems;
    public SidebarItem SettingsItem => _settingsItem;
}
