using Avalonia.Controls;
using FileHub.Desktop.Controls;
using FluentAvalonia.UI.Controls;

namespace FileHub.Desktop.Views.Controls;

/// <summary>
/// Files App's NavigationToolbar.xaml ported to Avalonia. The shell-level sidebar toggle
/// is exposed as a property because it is not part of the per-tab DataContext.
/// </summary>
public partial class FilesNavigationToolbar : UserControl
{
    public FilesNavigationToolbar() => InitializeComponent();

    public ToolbarIconButton SidebarToggleButton => _sidebarToggle;
    public FABreadcrumbBar Breadcrumb => _breadcrumb;
    public TextBox AddressBox => _addressBox;
    public TextBox SearchBox => _searchBox;
}
