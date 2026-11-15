using System.Diagnostics;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using FileHub.Desktop.Models;
using FileHub.Desktop.Services;
using FileHub.Desktop.ViewModels;
using FileHub.Files.Models;
using FileHub.Files.Services;
using FluentAvalonia.UI.Controls;

namespace FileHub.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _initialized;

    public MainWindow()
    {
        InitializeComponent();

        var dialogs = new DialogService(this);
        _vm = new MainViewModel(new UiServices
        {
            OpenFileAsync = path => Task.Run(() =>
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })),
            PromptAsync = dialogs.PromptAsync,
            ConfirmAsync = dialogs.ConfirmAsync,
            ShowInfoAsync = dialogs.ShowInfoAsync,
            CopyTextAsync = dialogs.CopyTextAsync,
            Reveal = PlatformService.Reveal,
            SetTheme = mode => (Application.Current as App)?.SetTheme(mode),
            SelectAll = () => (FileList.IsVisible ? FileList : GridList).SelectAll()
        });

        DataContext = _vm;
        _vm.LoadLocations();
        BuildNavigation(_vm.Locations);
        BuildContextMenus();
        WireEvents();

        Loaded += async (_, _) =>
        {
            if (_initialized) return;
            _initialized = true;
            await _vm.InitializeAsync();
        };
    }

    private void BuildNavigation(IEnumerable<LocationEntry> locations)
    {
        var quick = locations.Where(x => x.Kind == LocationKind.QuickAccess).ToArray();
        var drives = locations.Where(x => x.Kind == LocationKind.Drive).ToArray();

        if (quick.Length > 0) Nav.MenuItems.Add(new FANavigationViewItemHeader { Content = "快速访问" });
        foreach (var location in quick) Nav.MenuItems.Add(MakeNavItem(location));

        if (drives.Length > 0) Nav.MenuItems.Add(new FANavigationViewItemHeader { Content = "此电脑 / 卷" });
        foreach (var location in drives) Nav.MenuItems.Add(MakeNavItem(location));
    }

    private static FANavigationViewItem MakeNavItem(LocationEntry location)
    {
        var item = new FANavigationViewItem
        {
            Content = location.Name,
            DataContext = location,
            IconSource = new FASymbolIconSource { Symbol = MapSymbol(location.IconKey), FontSize = 16 }
        };
        ToolTip.SetTip(item, string.IsNullOrEmpty(location.Detail)
            ? location.Path
            : $"{location.Path}\n{location.Detail}");
        return item;
    }

    private static FASymbol MapSymbol(string key) => key switch
    {
        "Home" => FASymbol.Home,
        "View" => FASymbol.View,
        "Download" => FASymbol.Download,
        "Document" => FASymbol.Document,
        "Picture" => FASymbol.Image,
        "Video" => FASymbol.Video,
        "HardDrive" => FASymbol.MapDrive,
        _ => FASymbol.Folder
    };

    private void BuildContextMenus()
    {
        FileList.ContextFlyout = CreateContextMenu();
        GridList.ContextFlyout = CreateContextMenu();
    }

    private FAMenuFlyout CreateContextMenu()
    {
        var flyout = new FAMenuFlyout();

        void Add(string text, FASymbol symbol, ICommand command)
        {
            flyout.Items.Add(new FAMenuFlyoutItem
            {
                Text = text,
                IconSource = new FASymbolIconSource { Symbol = symbol, FontSize = 16 },
                Command = command
            });
        }

        Add("打开", FASymbol.OpenFile, _vm.OpenSelectedCommand);
        Add("在系统文件管理器中显示", FASymbol.OpenFolder, _vm.RevealCommand);
        flyout.Items.Add(new FAMenuFlyoutSeparator());
        Add("复制", FASymbol.Copy, _vm.CopyCommand);
        Add("剪切", FASymbol.Cut, _vm.CutCommand);
        Add("粘贴", FASymbol.Paste, _vm.PasteCommand);
        Add("复制路径", FASymbol.Link, _vm.CopyPathCommand);
        flyout.Items.Add(new FAMenuFlyoutSeparator());
        Add("新建文件夹", FASymbol.Add, _vm.NewFolderCommand);
        Add("重命名", FASymbol.Edit, _vm.RenameCommand);
        Add("移到回收站", FASymbol.Delete, _vm.DeleteCommand);
        flyout.Items.Add(new FAMenuFlyoutSeparator());
        Add("全选", FASymbol.Accept, _vm.SelectAllCommand);
        Add("属性", FASymbol.ContactInfo, _vm.PropertiesCommand);
        return flyout;
    }

    private void WireEvents()
    {
        Nav.SelectionChanged += (_, args) =>
        {
            if (args.SelectedItem is FANavigationViewItem { DataContext: LocationEntry location })
                _vm.NavigatePathCommand.Execute(location.Path);
        };

        Breadcrumb.ItemClicked += (_, args) =>
        {
            if (args.Item is PathSegment segment) _vm.NavigateSegmentCommand.Execute(segment);
        };

        FileList.SelectionChanged += (_, _) => SyncSelection(FileList);
        GridList.SelectionChanged += (_, _) => SyncSelection(GridList);
        FileList.DoubleTapped += OnDoubleTapped;
        GridList.DoubleTapped += OnDoubleTapped;

        KeyDown += OnWindowKeyDown;
        AddressBox.KeyDown += OnAddressKeyDown;
        SearchBox.KeyDown += OnSearchKeyDown;
    }

    private void SyncSelection(ListBox box)
        => _vm.SetSelection((box.SelectedItems ?? Array.Empty<object>())
            .OfType<FileEntryViewModel>().ToArray());

    private void OnDoubleTapped(object? sender, TappedEventArgs args)
    {
        if (sender is ListBox { SelectedItem: FileEntryViewModel entry }) _vm.OpenCommand.Execute(entry);
    }

    private void OnAddressKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter)
        {
            _vm.NavigateAddressCommand.Execute(null);
            args.Handled = true;
        }
        else if (args.Key == Key.Escape)
        {
            _vm.IsAddressEditing = false;
            args.Handled = true;
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter)
        {
            _vm.DeepSearchCommand.Execute(null);
            args.Handled = true;
        }
        else if (args.Key == Key.Escape)
        {
            _vm.SearchQuery = string.Empty;
            args.Handled = true;
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs args)
    {
        // 输入控件里不抢快捷键（Esc / Enter 由各自的 handler 处理）
        if (args.Source is TextBox) return;

        var ctrl = args.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                   args.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var alt = args.KeyModifiers.HasFlag(KeyModifiers.Alt);

        if (alt && args.Key == Key.Left) { _vm.GoBackCommand.Execute(null); args.Handled = true; return; }
        if (alt && args.Key == Key.Right) { _vm.GoForwardCommand.Execute(null); args.Handled = true; return; }

        if (ctrl)
        {
            switch (args.Key)
            {
                case Key.C: _vm.CopyCommand.Execute(null); args.Handled = true; return;
                case Key.X: _vm.CutCommand.Execute(null); args.Handled = true; return;
                case Key.V: _vm.PasteCommand.Execute(null); args.Handled = true; return;
                case Key.A: _vm.SelectAllCommand.Execute(null); args.Handled = true; return;
                case Key.F: SearchBox.Focus(); args.Handled = true; return;
                case Key.L:
                    _vm.IsAddressEditing = true;
                    AddressBox.Focus();
                    AddressBox.SelectAll();
                    args.Handled = true;
                    return;
            }
        }

        switch (args.Key)
        {
            case Key.F2: _vm.RenameCommand.Execute(null); args.Handled = true; break;
            case Key.F5: _vm.RefreshCommand.Execute(null); args.Handled = true; break;
            case Key.Delete: _vm.DeleteCommand.Execute(null); args.Handled = true; break;
            case Key.Back: _vm.GoUpCommand.Execute(null); args.Handled = true; break;
            case Key.Enter: _vm.OpenSelectedCommand.Execute(null); args.Handled = true; break;
            case Key.Escape: _vm.ClearSearchCommand.Execute(null); args.Handled = true; break;
        }
    }
}
