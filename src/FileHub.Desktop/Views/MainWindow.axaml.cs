using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using FileHub.Desktop.Actions;
using FileHub.Desktop.Controls;
using FileHub.Desktop.Models;
using FileHub.Desktop.Services;
using FileHub.Desktop.ViewModels;
using FileHub.Files.Models;
using FileHub.Files.Services;
using FluentAvalonia.UI.Controls;
using WinUI3.Fluent.Controls;

namespace FileHub.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;
    private readonly List<SidebarItem> _sidebarItems = [];
    private MainViewModel Vm => _shell.ActiveTab;
    private MainViewModel? _boundTab;
    private bool _initialized;

    // The four Files App UserControls own their named elements; keep the old short names
    // here so the wiring code matches Files' own code-behind style.
    private FABreadcrumbBar Breadcrumb => NavToolbar.Breadcrumb;
    private TextBox AddressBox => NavToolbar.AddressBox;
    private TextBox SearchBox => NavToolbar.SearchBox;
    private ListBox FileList => LayoutPage.FileList;
    private ListBox GridList => LayoutPage.GridList;
    private ItemsControl QuickItems => Sidebar.QuickItems;
    private ItemsControl DriveItems => Sidebar.DriveItems;
    private ToolbarIconButton SidebarToggleButton => NavToolbar.SidebarToggleButton;
    private Button DetailsToggleButton => StatusBar.DetailsToggleButton;

    public MainWindow()
    {
        InitializeComponent();

        _shell = ShellViewModel.Current;
        DataContext = _shell;
        _shell.EnsureFirstTab();
        Toolbar.Commands = _shell.Commands;

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
        NavToolbar.DataContext = _boundTab;
        StatusBar.DataContext = _boundTab;
        Sidebar.SettingsItem.Command = _boundTab.ToggleThemeCommand;
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

    /// <summary>
    /// Files builds context menus from its CommandManager; do the same so the menu and the
    /// toolbar always expose the same command identities. Commands are resolved lazily by
    /// CommandManager, so they always target the active tab.
    /// </summary>
    private FAMenuFlyout CreateContextMenu()
    {
        var flyout = new FAMenuFlyout();
        foreach (var command in _shell.Commands.ContextCommands)
        {
            flyout.Items.Add(new FAMenuFlyoutItem
            {
                Text = command.Label,
                IconSource = new FASymbolIconSource { Symbol = command.Symbol, FontSize = 16 },
                Command = command,
                CommandParameter = command.Parameter
            });
        }
        return flyout;
    }

    private void WireEvents()
    {
        // Shell-level commands that live outside the per-tab DataContext scope.
        SidebarToggleButton.Command = _shell.ToggleSidebarCommand;
        DetailsToggleButton.Command = _shell.ToggleDetailsPaneCommand;

        // The tab strip's own close button (WinUI3.Fluent WinTabStripItem.CloseRequested).
        TabStrip.AddHandler(WinTabStripItem.CloseRequestedEvent, OnTabCloseRequested);

        Breadcrumb.ItemClicked += (_, args) =>
        {
            if (args.Item is PathSegment segment) Vm.NavigateSegmentCommand.Execute(segment);
        };

        FileList.SelectionChanged += (_, _) => SyncSelection(FileList);
        GridList.SelectionChanged += (_, _) => SyncSelection(GridList);
        FileList.DoubleTapped += OnDoubleTapped;
        GridList.DoubleTapped += OnDoubleTapped;

        WireDragAndDrop();

        KeyDown += OnWindowKeyDown;
        AddressBox.KeyDown += OnAddressKeyDown;
        SearchBox.KeyDown += OnSearchKeyDown;
    }

    private void SyncSelection(ListBox box)
        => Vm.SetSelection((box.SelectedItems ?? Array.Empty<object>())
            .OfType<FileEntryViewModel>().ToArray());

    /// <summary>Tab close button; the item's DataContext is the tab it represents.</summary>
    private void OnTabCloseRequested(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if (args.Source is WinTabStripItem { DataContext: MainViewModel tab })
        {
            _shell.CloseTab(tab);
        }
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs args)
    {
        if (sender is ListBox { SelectedItem: FileEntryViewModel entry }) Vm.OpenCommand.Execute(entry);
    }

    /// <summary>
    /// Files App supports dragging items onto folders, breadcrumbs and sidebar locations. Here
    /// the two list views are the drag source and every folder-like surface is a drop target.
    /// Internal drags move; Ctrl turns them into a copy (same as Files). External file drags
    /// arriving from Finder/Explorer are imported as a copy.
    /// </summary>
    private void WireDragAndDrop()
    {
        foreach (var list in LayoutPage.FileViews)
        {
            DragDrop.SetAllowDrop(list, true);
            list.AddHandler(DragDrop.DragOverEvent, OnListDragOver);
            list.AddHandler(DragDrop.DragLeaveEvent, OnListDragLeave);
            list.AddHandler(DragDrop.DropEvent, OnListDrop);
            list.PointerPressed += OnListPointerPressed;
        }

        // Dropping on a breadcrumb segment navigates the payload into that folder.
        DragDrop.SetAllowDrop(Breadcrumb, true);
        Breadcrumb.AddHandler(DragDrop.DragOverEvent, OnPathTargetDragOver);
        Breadcrumb.AddHandler(DragDrop.DropEvent, OnBreadcrumbDrop);

        // Sidebar quick-access and drives are folder targets too.
        DragDrop.SetAllowDrop(Sidebar, true);
        Sidebar.AddHandler(DragDrop.DragOverEvent, OnPathTargetDragOver);
        Sidebar.AddHandler(DragDrop.DropEvent, OnSidebarDrop);
    }

    private async void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || sender is not ListBox list) return;

        // Find the entry under the pointer. Visual-tree walk is required because ListBoxItem
        // is generated and its DataContext is the row's FileEntryViewModel.
        var entry = FindAncestorDataContext<FileEntryViewModel>(e.Source as Control);
        if (entry is null) return;

        // Dragging an already-selected item carries the whole selection (multi-item drag).
        // Dragging an unselected item selects just that item first, matching Files App.
        // DoDragDropAsync owns the move threshold, so a plain click still selects normally.
        var selected = list.SelectedItems?.OfType<FileEntryViewModel>().ToArray() ?? [];
        if (!selected.Any(x => ReferenceEquals(x, entry)))
        {
            list.SelectedItems?.Clear();
            list.SelectedItems?.Add(entry);
            selected = [entry];
        }

        var sources = selected.Select(x => x.FullPath).ToArray();
        if (sources.Length == 0) return;

        var copy = e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                   e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var data = DragDropPayload.Create(sources, copy);
        await DragDrop.DoDragDropAsync(e, data,
            copy ? DragDropEffects.Copy : DragDropEffects.Move);
    }

    private static T? FindAncestorDataContext<T>(Control? control) where T : class
    {
        var current = control;
        while (current is not null)
        {
            if (current.DataContext is T value) return value;
            current = current.Parent as Control;
        }
        return null;
    }

    private void OnListDragOver(object? sender, DragEventArgs e)
    {
        if (!DragDropPayload.TryRead(e.DataTransfer, out var paths, out _))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        // Dropping on a folder row targets that folder; anywhere else targets the current folder.
        var entry = FindAncestorDataContext<FileEntryViewModel>(e.Source as Control);
        var destination = entry is { IsDirectory: true } ? entry.FullPath : Vm.CurrentPath;
        var invalid = paths.Any(p => FileSystemService.IsDescendantOrSelf(p, destination));
        e.DragEffects = invalid ? DragDropEffects.None
            : e.KeyModifiers.HasFlag(KeyModifiers.Control) ? DragDropEffects.Copy
            : DragDropEffects.Move;

        SetDropHighlight(e.Source as Control, entry is { IsDirectory: true } && !invalid);
    }

    private void OnListDragLeave(object? sender, DragEventArgs e) => SetDropHighlight(e.Source as Control, false);

    /// <summary>Toggles the drop-target pseudo-class on the folder row under the pointer.</summary>
    private static void SetDropHighlight(Control? source, bool active)
    {
        var current = source;
        while (current is not null)
        {
            if (current is ListBoxItem item)
            {
                item.Classes.Set("drop-target", active);
                return;
            }
            current = current.Parent as Control;
        }
    }

    private void OnListDrop(object? sender, DragEventArgs e)
    {
        SetDropHighlight(e.Source as Control, false);
        if (!DragDropPayload.TryRead(e.DataTransfer, out var paths, out var copy)) return;
        e.Handled = true;

        var entry = FindAncestorDataContext<FileEntryViewModel>(e.Source as Control);
        var destination = entry is { IsDirectory: true } ? entry.FullPath : Vm.CurrentPath;
        _ = Vm.DropIntoAsync(paths, destination, copy);
    }

    private void OnPathTargetDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropPayload.TryRead(e.DataTransfer, out _, out _)
            ? DragDropEffects.Move : DragDropEffects.None;
    }

    private void OnBreadcrumbDrop(object? sender, DragEventArgs e)
    {
        if (!DragDropPayload.TryRead(e.DataTransfer, out var paths, out var copy)) return;
        if (FindAncestorDataContext<PathSegment>(e.Source as Control) is not { } segment) return;
        e.Handled = true;
        _ = Vm.DropIntoAsync(paths, segment.FullPath, copy);
    }

    private void OnSidebarDrop(object? sender, DragEventArgs e)
    {
        if (!DragDropPayload.TryRead(e.DataTransfer, out var paths, out var copy)) return;
        if (FindAncestorDataContext<LocationEntry>(e.Source as Control) is not { } location) return;
        e.Handled = true;
        _ = Vm.DropIntoAsync(paths, location.Path, copy);
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

        // Hotkeys are resolved through the same CommandManager the toolbar and context menu
        // use, mirroring Files App's CommandManager[HotKey] lookup.
        var ctrl = args.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                   args.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var alt = args.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var shift = args.KeyModifiers.HasFlag(KeyModifiers.Shift);

        string? hotKey = null;
        if (alt && args.Key == Key.Left) hotKey = "Alt+Left";
        else if (alt && args.Key == Key.Right) hotKey = "Alt+Right";
        else if (alt && args.Key == Key.Up) hotKey = "Alt+Up";
        else if (ctrl && shift && args.Key == Key.N) hotKey = "Ctrl+Shift+N";
        else if (ctrl && shift && args.Key == Key.C) hotKey = "Ctrl+Shift+C";
        else if (ctrl && shift && args.Key == Key.H) hotKey = "Ctrl+Shift+H";
        else if (ctrl && args.Key == Key.C) hotKey = "Ctrl+C";
        else if (ctrl && args.Key == Key.X) hotKey = "Ctrl+X";
        else if (ctrl && args.Key == Key.V) hotKey = "Ctrl+V";
        else if (ctrl && args.Key == Key.A) hotKey = "Ctrl+A";
        else if (ctrl && args.Key == Key.F) { SearchBox.Focus(); args.Handled = true; return; }
        else if (ctrl && args.Key == Key.L) hotKey = "Ctrl+L";
        else if (ctrl && args.Key == Key.T) hotKey = "Ctrl+T";

        if (hotKey is not null && _shell.Commands[hotKey] is { } command &&
            command != Actions.CommandManager.None)
        {
            command.Execute(null);
            args.Handled = true;
            return;
        }

        switch (args.Key)
        {
            case Key.F2: _shell.Commands.Execute(CommandCodes.Rename); args.Handled = true; break;
            case Key.F5: _shell.Commands.Execute(CommandCodes.Refresh); args.Handled = true; break;
            case Key.Delete: _shell.Commands.Execute(CommandCodes.Delete); args.Handled = true; break;
            case Key.Back: _shell.Commands.Execute(CommandCodes.GoUp); args.Handled = true; break;
            case Key.Enter: _shell.Commands.Execute(CommandCodes.OpenSelected); args.Handled = true; break;
            case Key.Escape: Vm.ClearSearchCommand.Execute(null); args.Handled = true; break;
        }
    }
}
