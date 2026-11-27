using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FileHub.Code.Services;
using FileHub.Desktop.Actions;
using FileHub.Desktop.Controls;
using FileHub.Desktop.Models;
using FileHub.Desktop.Services;
using FileHub.Desktop.ViewModels;
using FileHub.Files.Models;
using FileHub.Files.Services;
using FileHub.Files.Vfs;
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
        // The palette can be opened from the keyboard, a button or a command; focus its input
        // whenever it becomes visible, regardless of who opened it.
        _shell.Palette.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CommandPaletteViewModel.IsOpen) && _shell.Palette.IsOpen)
                Dispatcher.UIThread.Post(() => PaletteBox.Focus(), DispatcherPriority.Input);
        };
        SyncActiveTab();
        ApplyShellVisibility();

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
        switch (e.PropertyName)
        {
            case nameof(ShellViewModel.ActiveTab):
                SyncActiveTab();
                break;
            case nameof(ShellViewModel.IsFilesChromeVisible):
            case nameof(ShellViewModel.IsCodePageOpen):
            case nameof(ShellViewModel.IsOfficePageOpen):
                ApplyShellVisibility();
                break;
        }
    }

    /// <summary>
    /// Applies shell-level visibility to regions whose DataContext is something else: the
    /// navigation toolbar, content area and status bar are re-pointed at the active tab in
    /// SyncActiveTab, and the three workspace pages own their view models. Binding these in XAML
    /// therefore resolved against the wrong object (silently failing, and leaving the code editor,
    /// extensions and Office pages stacked on top of each other). Setting them here is explicit.
    /// </summary>
    private void ApplyShellVisibility()
    {
        // Document mode: only the document and the sidebar stay on screen.
        var chrome = _shell.IsFilesChromeVisible;
        NavToolbar.IsVisible = chrome;
        InnerContent.IsVisible = chrome;
        StatusBar.IsVisible = chrome;

        CodeEditorView.IsVisible = _shell.IsCodePageOpen;
        DocumentView.IsVisible = _shell.IsOfficePageOpen;
    }

    /// <summary>Points the content area, address bar and status bar at the active tab.</summary>
    private void SyncActiveTab()
    {
        if (_boundTab is not null)
        {
            _boundTab.PropertyChanged -= OnTabPropertyChanged;
            _boundTab.Locations.CollectionChanged -= OnLocationsChanged;
        }
        _boundTab = _shell.ActiveTab;
        _boundTab.PropertyChanged += OnTabPropertyChanged;
        // Adding a network location must show up immediately, not after a restart.
        _boundTab.Locations.CollectionChanged += OnLocationsChanged;

        // These three regions bind against the tab itself. Assigning DataContext here (instead of
        // a XAML "ActiveTab.X" path on the window-level DataContext) keeps every nested binding
        // resolving, and makes a tab switch re-point all of them in one place.
        InnerContent.DataContext = _boundTab;
        NavToolbar.DataContext = _boundTab;
        StatusBar.DataContext = _boundTab;
        Sidebar.SettingsItem.Command = _shell.OpenSettingsCommand;
        Breadcrumb.ItemsSource = _boundTab.Breadcrumbs;
        SyncAddressMode();

        // Each tab keeps its own selection and sidebar location; restore their visuals on switch.
        // The lists may still be loading, so re-apply once they finish.
        RestoreTabVisuals();
    }

    private void RestoreTabVisuals()
    {
        _restoringSelection = true;
        try
        {
            var paths = Vm.SelectedEntries.Select(x => x.FullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var list in LayoutPage.FileViews)
            {
                list.SelectedItems?.Clear();
                foreach (var entry in list.Items.OfType<FileEntryViewModel>())
                    if (paths.Contains(entry.FullPath)) list.SelectedItems?.Add(entry);
            }
        }
        finally { _restoringSelection = false; }

        var current = Vm.CurrentPath;
        foreach (var item in _sidebarItems)
        {
            if (item.DataContext is not LocationEntry location) continue;
            var selected = !string.IsNullOrEmpty(current) &&
                (string.Equals(location.Path, current, StringComparison.OrdinalIgnoreCase) ||
                 current.StartsWith(location.Path.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
            if (selected) item.IsSelected = true;
        }
    }

    private void OnTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsAddressEditing)) SyncAddressMode();
        else if (e.PropertyName == nameof(MainViewModel.IsLoading) && !Vm.IsLoading) RestoreTabVisuals();
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
    private void OnLocationsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => BuildNavigation(Vm.Locations);

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

    /// <summary>
    /// Two context menus, rebuilt on every right click: one for a file/folder and one for empty
    /// space. Files' CommandManager supplies the command identities, but the menu layout is
    /// hand-built so it can show separators, submenus, check marks and shortcut hints - the
    /// flat command list it replaced could not express any of that.
    /// </summary>
    private void BuildContextMenus()
    {
        foreach (var list in LayoutPage.FileViews)
        {
            list.ContextFlyout = CreateBackgroundMenu();
            list.AddHandler(PointerPressedEvent, OnListRightPressed, RoutingStrategies.Tunnel);
            list.AddHandler(ContextRequestedEvent, OnListContextRequested, RoutingStrategies.Tunnel);
        }
    }

    /// <summary>Right click: select the item under the pointer before the flyout opens.</summary>
    private void OnListRightPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBox list) return;
        if (!e.GetCurrentPoint(list).Properties.IsRightButtonPressed) return;

        var hit = FindAncestor<ListBoxItem>(e.Source as Visual, list);
        var entry = hit?.DataContext as FileEntryViewModel;
        if (entry is null)
        {
            list.SelectedItems?.Clear();
            Vm.SetSelection([]);
            list.ContextFlyout = CreateBackgroundMenu();
            return;
        }

        var selected = list.SelectedItems;
        if (selected is null || !selected.Contains(entry))
        {
            selected?.Clear();
            selected?.Add(entry);
        }
        list.ContextFlyout = CreateItemMenu(entry);
    }

    /// <summary>
    /// Keyboard menu key / Shift+F10 has no pointer position; fall back to the current selection
    /// and make sure the assigned flyout matches it.
    /// </summary>
    private void OnListContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not ListBox list) return;

        FileEntryViewModel? entry = null;
        if (e.TryGetPosition(list, out var point))
        {
            // Pointer-opened menu: trust the position, so right-clicking blank space never falls
            // back to the selection that happens to be active.
            entry = FindAncestor<ListBoxItem>(list.InputHitTest(point) as Visual, list)?.DataContext
                as FileEntryViewModel;
        }
        else
        {
            entry = list.SelectedItem as FileEntryViewModel;
        }

        list.ContextFlyout = entry is null ? CreateBackgroundMenu() : CreateItemMenu(entry);
    }

    private static T? FindAncestor<T>(Visual? source, Visual stop) where T : Visual
    {
        while (source is not null && !ReferenceEquals(source, stop))
        {
            if (source is T match) return match;
            source = source.GetVisualParent();
        }
        return null;
    }

    // ------------------------------------------------------------------ item menu

    private FAMenuFlyout CreateItemMenu(FileEntryViewModel entry)
    {
        var flyout = new FAMenuFlyout();
        var tab = Vm;
        var selectionCount = Math.Max(1, tab.SelectedEntries.Count);
        var multi = selectionCount > 1;
        var isDir = entry.IsDirectory;
        var isLocal = !VfsPath.IsVirtualPath(entry.FullPath);
        var isArchive = !isDir && isLocal && ArchiveService.IsArchiveExtension(entry.FullPath);

        flyout.Items.Add(MenuItem(Loc.T("CtxOpen"), FASymbol.OpenFile, tab.OpenCommand, entry, "Enter"));

        var openWith = new FAMenuFlyoutSubItem
        {
            Text = Loc.T("CtxOpenWith"),
            IconSource = new FASymbolIconSource { Symbol = FASymbol.OpenFile, FontSize = 16 }
        };
        if (!isDir && CodeLanguageService.IsCodeFile(entry.FullPath))
        {
            openWith.Items.Add(MenuItem(Loc.T("CtxOpenCode"), FASymbol.Code,
                new RelayCommand(_ => _ = _shell.OpenCodeFileAsync(entry.FullPath))));
        }
        if (!isDir && FileHub.Office.Services.OfficeDocumentService.IsOfficeFile(entry.FullPath))
        {
            openWith.Items.Add(MenuItem(Loc.T("CtxOpenOffice"), FASymbol.Document,
                new RelayCommand(_ => _ = _shell.OpenOfficeFileAsync(entry.FullPath))));
        }
        openWith.Items.Add(MenuItem(Loc.T("CtxOpenPreview"), FASymbol.View,
            new RelayCommand(_ => _ = _shell.OpenQuickLookAsync(entry.FullPath))));
        openWith.Items.Add(new FAMenuFlyoutSeparator());
        openWith.Items.Add(MenuItem(Loc.T("CtxOpenSystem"), FASymbol.OpenFolder,
            new RelayCommand(_ => _ = _shell.OpenWithSystemAsync(entry.FullPath))));
        flyout.Items.Add(openWith);

        if (isDir)
        {
            flyout.Items.Add(MenuItem(Loc.T("CtxOpenNewTab"), FASymbol.Add,
                new RelayCommand(_ => _shell.AddTab(entry.FullPath))));
        }

        flyout.Items.Add(new FAMenuFlyoutSeparator());
        flyout.Items.Add(MenuItem(Loc.T("LblCut"), FASymbol.Cut, tab.CutCommand, null, "Ctrl+X"));
        flyout.Items.Add(MenuItem(Loc.T("LblCopy"), FASymbol.Copy, tab.CopyCommand, null, "Ctrl+C"));
        flyout.Items.Add(MenuItem(Loc.T("LblPaste"), FASymbol.Paste, tab.PasteCommand, null, "Ctrl+V"));

        if (multi)
        {
            flyout.Items.Add(MenuItem(Loc.T("BatchRename"), FASymbol.Edit,
                new RelayCommand(_ => _ = tab.OpenBatchRenameAsync())));
        }
        else
        {
            flyout.Items.Add(MenuItem(Loc.T("LblRename"), FASymbol.Edit, tab.RenameCommand, null, "F2"));
        }
        flyout.Items.Add(MenuItem(Loc.T("LblDelete"), FASymbol.Delete, tab.DeleteCommand, null, "Delete"));

        flyout.Items.Add(new FAMenuFlyoutSeparator());
        flyout.Items.Add(MenuItem(Loc.T("Compress"), FASymbol.ZipFolder,
            new RelayCommand(_ => _ = tab.CompressSelectionAsync())));
        if (isArchive)
        {
            flyout.Items.Add(MenuItem(Loc.T("Extract"), FASymbol.OpenFolder,
                new RelayCommand(_ => _ = tab.ExtractArchiveAsync())));
        }

        flyout.Items.Add(new FAMenuFlyoutSeparator());
        flyout.Items.Add(MenuItem(Loc.T("CtxCopyPath"), FASymbol.Link, tab.CopyPathCommand, null, "Ctrl+Shift+C"));
        flyout.Items.Add(MenuItem(Loc.T("CtxCopyName"), FASymbol.Document, tab.CopyNameCommand));
        if (isLocal)
        {
            flyout.Items.Add(MenuItem(Loc.T("CtxReveal"), FASymbol.OpenFolder, tab.RevealCommand));
        }
        if (isDir && isLocal)
        {
            flyout.Items.Add(MenuItem(Loc.T("CtxOpenTerminal"), FASymbol.Code, tab.OpenTerminalCommand));
        }

        flyout.Items.Add(new FAMenuFlyoutSeparator());
        flyout.Items.Add(MenuItem(Loc.T("LblProperties"), FASymbol.ContactInfo, tab.PropertiesCommand, null, "Alt+Enter"));
        return flyout;
    }

    // ------------------------------------------------------------------ background menu

    private FAMenuFlyout CreateBackgroundMenu()
    {
        var flyout = new FAMenuFlyout();
        var tab = Vm;

        flyout.Items.Add(MenuItem(Loc.T("LblNew"), FASymbol.Add, tab.NewFolderCommand, null, "Ctrl+Shift+N"));
        flyout.Items.Add(MenuItem(Loc.T("LblPaste"), FASymbol.Paste, tab.PasteCommand, null, "Ctrl+V"));
        flyout.Items.Add(MenuItem(Loc.T("Refresh"), FASymbol.Refresh, tab.RefreshCommand, null, "F5"));
        flyout.Items.Add(new FAMenuFlyoutSeparator());

        var sort = new FAMenuFlyoutSubItem
        {
            Text = Loc.T("CtxSortBy"),
            IconSource = new FASymbolIconSource { Symbol = FASymbol.Sort, FontSize = 16 }
        };
        sort.Items.Add(CheckedItem(Loc.T("SortName"), tab.IsSortedBy("Name"),
            new RelayCommand(_ => tab.SetSortCommand.Execute("Name"))));
        sort.Items.Add(CheckedItem(Loc.T("SortModified"), tab.IsSortedBy("Modified"),
            new RelayCommand(_ => tab.SetSortCommand.Execute("Modified"))));
        sort.Items.Add(CheckedItem(Loc.T("SortSize"), tab.IsSortedBy("Size"),
            new RelayCommand(_ => tab.SetSortCommand.Execute("Size"))));
        sort.Items.Add(CheckedItem(Loc.T("SortKind"), tab.IsSortedBy("Kind"),
            new RelayCommand(_ => tab.SetSortCommand.Execute("Kind"))));
        sort.Items.Add(new FAMenuFlyoutSeparator());
        sort.Items.Add(CheckedItem(Loc.T("CtxSortAsc"), !tab.SortDescending,
            new RelayCommand(_ => tab.SetSortCommand.Execute(tab.SortColumn + ":asc"))));
        sort.Items.Add(CheckedItem(Loc.T("CtxSortDesc"), tab.SortDescending,
            new RelayCommand(_ => tab.SetSortCommand.Execute(tab.SortColumn + ":desc"))));
        flyout.Items.Add(sort);

        var view = new FAMenuFlyoutSubItem
        {
            Text = Loc.T("CtxView"),
            IconSource = new FASymbolIconSource { Symbol = FASymbol.ViewAll, FontSize = 16 }
        };
        view.Items.Add(CheckedItem(Loc.T("CtxViewDetails"), tab.IsDetailsView,
            new RelayCommand(_ => tab.SetViewCommand.Execute("list"))));
        view.Items.Add(CheckedItem(Loc.T("CtxViewIcons"), tab.IsGridView,
            new RelayCommand(_ => tab.SetViewCommand.Execute("grid"))));
        flyout.Items.Add(view);

        flyout.Items.Add(CheckedItem(Loc.T("LblHidden"), tab.ShowHiddenFiles, tab.ToggleHiddenCommand,
            "Ctrl+H"));
        flyout.Items.Add(MenuItem(Loc.T("LblSelect"), FASymbol.ViewAll, tab.SelectAllCommand, null, "Ctrl+A"));

        flyout.Items.Add(new FAMenuFlyoutSeparator());
        flyout.Items.Add(MenuItem(Loc.T("CtxOpenTerminal"), FASymbol.Code, tab.OpenTerminalCommand));
        flyout.Items.Add(MenuItem(Loc.T("LblSettings"), FASymbol.Settings, _shell.OpenSettingsCommand, null, "Ctrl+,"));
        return flyout;
    }

    // ------------------------------------------------------------------ helpers

    private static FAMenuFlyoutItem MenuItem(
        string text, FASymbol symbol, ICommand command, object? parameter = null, string? hotKey = null)
    {
        var item = new FAMenuFlyoutItem
        {
            Text = text,
            IconSource = new FASymbolIconSource { Symbol = symbol, FontSize = 16 },
            Command = command,
            CommandParameter = parameter
        };
        if (!string.IsNullOrEmpty(hotKey))
        {
            try { item.HotKey = KeyGesture.Parse(hotKey); }
            catch (FormatException) { }
        }
        return item;
    }

    /// <summary>A menu entry with a tick in the icon slot when it is the current state.</summary>
    private static FAMenuFlyoutItem CheckedItem(
        string text, bool isChecked, ICommand command, string? hotKey = null)
    {
        var item = new FAMenuFlyoutItem
        {
            Text = text,
            IconSource = isChecked
                ? new FASymbolIconSource { Symbol = FASymbol.Accept, FontSize = 14 }
                : null,
            Command = command
        };
        if (!string.IsNullOrEmpty(hotKey))
        {
            try { item.HotKey = KeyGesture.Parse(hotKey); }
            catch (FormatException) { }
        }
        return item;
    }

    private void WireEvents()
    {
        // Shell-level commands that live outside the per-tab DataContext scope.
        SidebarToggleButton.Command = _shell.ToggleSidebarCommand;
        DetailsToggleButton.Command = _shell.ToggleDetailsPaneCommand;

        // The tab strip's own close button (WinUI3.Fluent WinTabStripItem.CloseRequested).
        TabStrip.AddHandler(WinTabStripItem.CloseRequestedEvent, OnTabCloseRequested);
        TabStrip.AddHandler(WinTabStripItem.ContextMenuRequestedEvent, OnTabContextMenuRequested);
        TabStrip.AddHandler(WinTabStrip.TabReorderedEvent, OnTabReordered);
        Closing += (_, _) => _shell.SaveSession();

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
    {
        // RestoreTabVisuals re-selects items programmatically; do not feed that back into the VM.
        if (_restoringSelection) return;
        Vm.SetSelection((box.SelectedItems ?? Array.Empty<object>())
            .OfType<FileEntryViewModel>().ToArray());
    }
    private bool _restoringSelection;

    /// <summary>Tab close button / middle click; the item's DataContext is the tab it represents.</summary>
    private void OnTabCloseRequested(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if (args.Source is WinTabStripItem { DataContext: MainViewModel tab })
        {
            _shell.CloseTab(tab);
        }
    }

    /// <summary>Right-click a tab: activate it and open a Files-style tab context menu.</summary>
    private void OnTabContextMenuRequested(object? sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if (args.Source is not WinTabStripItem { DataContext: MainViewModel tab }) return;
        _shell.Activate(tab);

        var menu = new FAMenuFlyout();
        menu.Items.Add(MenuItem(Loc.T("NewTab"), FASymbol.Add, _shell.NewTabCommand, null, "Ctrl+T"));
        menu.Items.Add(MenuItem(Loc.T("DuplicateTab"), FASymbol.Copy,
            new RelayCommand(_ => _shell.DuplicateActiveTab()), null, "Ctrl+Shift+D"));
        menu.Items.Add(new FAMenuFlyoutSeparator());
        menu.Items.Add(MenuItem(Loc.T("CloseTab"), FASymbol.Delete,
            new RelayCommand(_ => _shell.CloseActiveTab()), null, "Ctrl+W"));
        menu.Items.Add(MenuItem(Loc.T("CloseOtherTabs"), FASymbol.Delete,
            new RelayCommand(_ => _shell.CloseOtherTabs(tab))));
        menu.Items.Add(MenuItem(Loc.T("CloseTabsToRight"), FASymbol.Delete,
            new RelayCommand(_ => _shell.CloseTabsToRight(tab))));
        menu.Items.Add(new FAMenuFlyoutSeparator());
        menu.Items.Add(MenuItem(Loc.T("PreviousTab"), FASymbol.Back,
            new RelayCommand(_ => _shell.ActivatePreviousTab()), null, "Ctrl+Shift+Tab"));
        menu.Items.Add(MenuItem(Loc.T("NextTab"), FASymbol.ChevronRight,
            new RelayCommand(_ => _shell.ActivateNextTab()), null, "Ctrl+Tab"));
        menu.ShowAt(args.Source as Control ?? TabStrip);
    }

    /// <summary>Drag-reorder: the strip reports source and destination indices.</summary>
    private void OnTabReordered(object? sender, TabReorderEventArgs args)
    {
        args.Handled = true;
        if (args.FromIndex >= 0 && args.FromIndex < _shell.Tabs.Count)
            _shell.MoveTab(_shell.Tabs[args.FromIndex], args.ToIndex);
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs args)
    {
        if (sender is not ListBox { SelectedItem: FileEntryViewModel entry }) return;
        if (!entry.IsDirectory && CodeLanguageService.IsCodeFile(entry.FullPath))
        {
            _ = _shell.OpenCodeFileAsync(entry.FullPath);
            return;
        }
        Vm.OpenCommand.Execute(entry);
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

    private void OpenPalette()
    {
        _shell.Palette.Open();
        Dispatcher.UIThread.Post(() => PaletteBox.Focus(), DispatcherPriority.Input);
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
        // Overlays own the keyboard while they are open, including keys that would otherwise be
        // swallowed by the palette's own text box.
        if (_shell.Palette.IsOpen)
        {
            switch (args.Key)
            {
                case Key.Escape:
                    _shell.Palette.Close();
                    args.Handled = true;
                    break;
                case Key.Enter:
                    _ = _shell.Palette.ExecuteSelectedAsync();
                    args.Handled = true;
                    break;
                case Key.Down:
                    _shell.Palette.MoveSelection(1);
                    args.Handled = true;
                    break;
                case Key.Up:
                    _shell.Palette.MoveSelection(-1);
                    args.Handled = true;
                    break;
            }
            return;
        }

        if (_shell.QuickLook.IsOpen)
        {
            switch (args.Key)
            {
                case Key.Escape:
                case Key.Space:
                    _shell.QuickLook.Close();
                    break;
                case Key.Right:
                case Key.Down:
                    _ = _shell.QuickLookStepAsync(1);
                    break;
                case Key.Left:
                case Key.Up:
                    _ = _shell.QuickLookStepAsync(-1);
                    break;
            }
            args.Handled = true;
            return;
        }

        // Do not steal shortcuts while typing in a text box.
        if (args.Source is TextBox) return;

        // Command palette must work on every page, before any file-manager shortcut.
        if ((args.KeyModifiers.HasFlag(KeyModifiers.Control) || args.KeyModifiers.HasFlag(KeyModifiers.Meta))
            && args.KeyModifiers.HasFlag(KeyModifiers.Shift) && args.Key == Key.P)
        {
            OpenPalette();
            args.Handled = true;
            return;
        }

        var workspaceCtrl = args.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                            args.KeyModifiers.HasFlag(KeyModifiers.Meta);

        // Settings dialog, the shortcut every other desktop app uses for it.
        if (workspaceCtrl && args.Key == Key.OemComma)
        {
            _shell.OpenSettingsCommand.Execute(null);
            args.Handled = true;
            return;
        }
        if (_shell.IsCodePageOpen && workspaceCtrl && args.Key == Key.S)
        {
            if (_shell.CodeEditor.SaveCommand.CanExecute(null)) _shell.CodeEditor.SaveCommand.Execute(null);
            args.Handled = true;
            return;
        }
        if (_shell.IsOfficePageOpen && workspaceCtrl && args.Key == Key.S)
        {
            if (_shell.Office.SaveCommand.CanExecute(null)) _shell.Office.SaveCommand.Execute(null);
            args.Handled = true;
            return;
        }

        // Hotkeys are resolved through the same CommandManager the toolbar and context menu
        // use, mirroring Files App's CommandManager[HotKey] lookup.
        var ctrl = args.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                   args.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var alt = args.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var shift = args.KeyModifiers.HasFlag(KeyModifiers.Shift);

        // Tab management works on every page, including the code editor.
        if (ctrl && !alt && args.Key == Key.Tab)
        {
            if (shift) _shell.ActivatePreviousTab(); else _shell.ActivateNextTab();
            args.Handled = true;
            return;
        }
        if (ctrl && shift && args.Key == Key.T) { _shell.ReopenClosedTab(); args.Handled = true; return; }
        if (ctrl && shift && args.Key == Key.D) { _shell.DuplicateActiveTab(); args.Handled = true; return; }
        if (ctrl && !alt && args.Key == Key.W)
        {
            // The tab strip is hidden in document mode, so Ctrl+W means "close the document".
            if (_shell.IsOfficePageOpen) _shell.CloseWorkspacePage();
            else _shell.CloseActiveTab();
            args.Handled = true;
            return;
        }
        if (ctrl && shift && args.Key == Key.Left) { _shell.MoveActiveTab(-1); args.Handled = true; return; }
        if (ctrl && shift && args.Key == Key.Right) { _shell.MoveActiveTab(1); args.Handled = true; return; }
        if (ctrl && !alt && !shift && args.Key >= Key.D1 && args.Key <= Key.D8)
        {
            _shell.ActivateByIndex(args.Key - Key.D1 + 1);
            args.Handled = true;
            return;
        }

        // File-manager hotkeys must not fire while a full-area page is on top.
        if (_shell.IsWorkspacePageOpen) return;

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
            case Key.Space: _ = _shell.ToggleQuickLookAsync(); args.Handled = true; break;
            case Key.Escape: Vm.ClearSearchCommand.Execute(null); args.Handled = true; break;
        }
    }
}
