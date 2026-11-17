using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using FileHub.Desktop.Controls;
using FileHub.Desktop.Models;
using FileHub.Desktop.Services;
using FileHub.Desktop.ViewModels;
using FileHub.Files.Models;
using FluentAvalonia.UI.Controls;

namespace FileHub.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;
    private readonly List<SidebarItem> _sidebarItems = [];
    private MainViewModel Vm => _shell.ActiveTab;
    private MainViewModel? _boundTab;
    private bool _initialized;

    public MainWindow()
    {
        InitializeComponent();

        _shell = ShellViewModel.Current;
        DataContext = _shell;
        _shell.EnsureFirstTab();

        // Content area follows the active tab. Done in code because binding DataContext to a
        // property of the inherited DataContext did not resolve reliably in Avalonia 12, and a
        // tab switch has to re-point it anyway.
        _shell.PropertyChanged += OnShellPropertyChanged;
        SyncActiveTab();

        Vm.LoadLocations();
        BuildNavigation(Vm.Locations);
        BuildContextMenus();
        WireEvents();

        Loaded += async (_, _) =>
        {
            if (_initialized) return;
            _initialized = true;
            await _shell.InitializeAsync();
        };
    }

    private void OnShellPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.ActiveTab)) SyncActiveTab();
    }

    /// <summary>Points the content area, address bar and status bar at the active tab.</summary>
    private void SyncActiveTab()
    {
        if (_boundTab is not null) _boundTab.PropertyChanged -= OnTabPropertyChanged;
        _boundTab = _shell.ActiveTab;
        _boundTab.PropertyChanged += OnTabPropertyChanged;

        // These three regions bind against the tab itself. Assigning DataContext here (instead of
        // a XAML "ActiveTab.X" path on the window-level DataContext) keeps every nested binding
        // resolving, and makes a tab switch re-point all of them in one place.
        InnerContent.DataContext = _boundTab;
        AddressBarRoot.DataContext = _boundTab;
        StatusBarRoot.DataContext = _boundTab;
        Breadcrumb.ItemsSource = _boundTab.Breadcrumbs;
        SyncAddressMode();

        FileList.SelectedItems?.Clear();
        GridList.SelectedItems?.Clear();
    }

    private void OnTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsAddressEditing)) SyncAddressMode();
    }

    /// <summary>Swaps the breadcrumb for the editable path box, mirroring Files App's omnibar.</summary>
    private void SyncAddressMode()
    {
        var editing = _boundTab?.IsAddressEditing == true;
        Breadcrumb.IsVisible = !editing;
        AddressBox.IsVisible = editing;

        if (editing)
        {
            // Focus after the box becomes visible, otherwise Focus() is a no-op.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                AddressBox.Focus();
                AddressBox.SelectAll();
            }, Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    /// <summary>Called by UiServices.SelectAll, which resolves the current window lazily.</summary>
    public void SelectAllEntries() => (FileList.IsVisible ? FileList : GridList).SelectAll();

    /// <summary>
    /// Builds the sidebar the way Files App builds SidebarView items: grouped sections with a
    /// header, compact rows and a footer item. FANavigationView cannot reproduce that layout,
    /// so the library's SidebarItem equivalent (FileHub.Desktop.Controls.SidebarItem) is used.
    /// </summary>
    private void BuildNavigation(IEnumerable<LocationEntry> locations)
    {
        var quick = locations.Where(x => x.Kind == LocationKind.QuickAccess).ToArray();
        var drives = locations.Where(x => x.Kind == LocationKind.Drive).ToArray();

        var quickItems = quick.Select(MakeSidebarItem).ToArray();
        var driveItems = drives.Select(MakeSidebarItem).ToArray();
        QuickItems.ItemsSource = quickItems;
        DriveItems.ItemsSource = driveItems;
        _sidebarItems.AddRange(quickItems);
        _sidebarItems.AddRange(driveItems);

        if (_sidebarItems.Count > 0) _sidebarItems[0].IsSelected = true;
    }

    private SidebarItem MakeSidebarItem(LocationEntry location)
    {
        var item = new SidebarItem
        {
            Symbol = MapSymbol(location.IconKey),
            Text = location.NameKey is null ? location.Name : Loc.T(location.NameKey),
            Detail = location.Detail ?? string.Empty,
            DataContext = location
        };
        ToolTip.SetTip(item, string.IsNullOrEmpty(location.Detail)
            ? location.Path
            : $"{location.Path}\n{location.Detail}");
        item.Command = new RelayCommand(_ =>
        {
            foreach (var other in _sidebarItems) other.IsSelected = ReferenceEquals(other, item);
            Vm.NavigatePathCommand.Execute(location.Path);
        });
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

        // Commands are resolved at execution time so the menu always targets the active tab.
        void Add(string key, FASymbol symbol, Func<ICommand> command)
        {
            flyout.Items.Add(new FAMenuFlyoutItem
            {
                Text = Loc.T(key),
                IconSource = new FASymbolIconSource { Symbol = symbol, FontSize = 16 },
                Command = new RelayCommand(p => command().Execute(p))
            });
        }

        Add("CtxOpen", FASymbol.OpenFile, () => Vm.OpenSelectedCommand);
        Add("CtxReveal", FASymbol.OpenFolder, () => Vm.RevealCommand);
        flyout.Items.Add(new FAMenuFlyoutSeparator());
        Add("CtxCopy", FASymbol.Copy, () => Vm.CopyCommand);
        Add("CtxCut", FASymbol.Cut, () => Vm.CutCommand);
        Add("CtxPaste", FASymbol.Paste, () => Vm.PasteCommand);
        Add("CtxCopyPath", FASymbol.Link, () => Vm.CopyPathCommand);
        flyout.Items.Add(new FAMenuFlyoutSeparator());
        Add("CtxNewFolder", FASymbol.Add, () => Vm.NewFolderCommand);
        Add("CtxRename", FASymbol.Edit, () => Vm.RenameCommand);
        Add("CtxDelete", FASymbol.Delete, () => Vm.DeleteCommand);
        flyout.Items.Add(new FAMenuFlyoutSeparator());
        Add("CtxSelectAll", FASymbol.Accept, () => Vm.SelectAllCommand);
        Add("CtxProperties", FASymbol.ContactInfo, () => Vm.PropertiesCommand);
        return flyout;
    }

    private void WireEvents()
    {
        // Shell-level commands that live outside the per-tab DataContext scope.
        SidebarToggleButton.Command = _shell.ToggleSidebarCommand;
        DetailsToggleButton.Command = _shell.ToggleDetailsPaneCommand;

        // The tab strip's own x-to-close button.
        TabView.TabCloseRequested += (_, args) =>
        {
            if (args.Item is MainViewModel tab) _shell.CloseTab(tab);
        };

        Breadcrumb.ItemClicked += (_, args) =>
        {
            if (args.Item is PathSegment segment) Vm.NavigateSegmentCommand.Execute(segment);
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
        => Vm.SetSelection((box.SelectedItems ?? Array.Empty<object>())
            .OfType<FileEntryViewModel>().ToArray());

    private void OnDoubleTapped(object? sender, TappedEventArgs args)
    {
        if (sender is ListBox { SelectedItem: FileEntryViewModel entry }) Vm.OpenCommand.Execute(entry);
    }

    private void OnAddressKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter)
        {
            Vm.NavigateAddressCommand.Execute(null);
            args.Handled = true;
        }
        else if (args.Key == Key.Escape)
        {
            Vm.IsAddressEditing = false;
            args.Handled = true;
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter)
        {
            Vm.DeepSearchCommand.Execute(null);
            args.Handled = true;
        }
        else if (args.Key == Key.Escape)
        {
            Vm.SearchQuery = string.Empty;
            args.Handled = true;
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs args)
    {
        // Do not steal shortcuts while typing in a text box.
        if (args.Source is TextBox) return;

        var ctrl = args.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                   args.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var alt = args.KeyModifiers.HasFlag(KeyModifiers.Alt);

        if (alt && args.Key == Key.Left) { Vm.GoBackCommand.Execute(null); args.Handled = true; return; }
        if (alt && args.Key == Key.Right) { Vm.GoForwardCommand.Execute(null); args.Handled = true; return; }

        if (ctrl)
        {
            switch (args.Key)
            {
                case Key.C: Vm.CopyCommand.Execute(null); args.Handled = true; return;
                case Key.X: Vm.CutCommand.Execute(null); args.Handled = true; return;
                case Key.V: Vm.PasteCommand.Execute(null); args.Handled = true; return;
                case Key.A: Vm.SelectAllCommand.Execute(null); args.Handled = true; return;
                case Key.F: SearchBox.Focus(); args.Handled = true; return;
                case Key.L:
                    Vm.IsAddressEditing = true;
                    AddressBox.Focus();
                    AddressBox.SelectAll();
                    args.Handled = true;
                    return;
            }
        }

        switch (args.Key)
        {
            case Key.F2: Vm.RenameCommand.Execute(null); args.Handled = true; break;
            case Key.F5: Vm.RefreshCommand.Execute(null); args.Handled = true; break;
            case Key.Delete: Vm.DeleteCommand.Execute(null); args.Handled = true; break;
            case Key.Back: Vm.GoUpCommand.Execute(null); args.Handled = true; break;
            case Key.Enter: Vm.OpenSelectedCommand.Execute(null); args.Handled = true; break;
            case Key.Escape: Vm.ClearSearchCommand.Execute(null); args.Handled = true; break;
        }
    }
}
