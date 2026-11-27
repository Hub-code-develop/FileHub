using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using Avalonia.Threading;
using FileHub.Desktop.Models;
using FileHub.Desktop.Services;
using FileHub.Desktop.Views;
using FileHub.Files.Models;
using FileHub.Files.Services;
using FileHub.Files.Vfs;
using FluentAvalonia.UI.Controls;

namespace FileHub.Desktop.ViewModels;

/// <summary>UI capabilities injected so the ViewModel never depends on Window / TopLevel.</summary>
public sealed class UiServices
{
    public Func<string, Task> OpenFileAsync { get; init; } = _ => Task.CompletedTask;
    public Func<string, string, string, string, Task<string?>> PromptAsync { get; init; }
        = (_, _, _, _) => Task.FromResult<string?>(null);
    public Func<string, string, Task<bool>> ConfirmAsync { get; init; } = (_, _) => Task.FromResult(false);
    public Func<string, string, Task> ShowInfoAsync { get; init; } = (_, _) => Task.CompletedTask;
    public Func<string, Task> CopyTextAsync { get; init; } = _ => Task.CompletedTask;
    public Action<string> Reveal { get; init; } = _ => { };
    public Action<string> SetTheme { get; init; } = _ => { };
    public Action SelectAll { get; init; } = () => { };
}

public sealed class MainViewModel : ViewModelBase
{
    private readonly UiServices _ui;
    private readonly FileClipboard _clipboard = new();
    private readonly List<string> _back = [];
    private readonly List<string> _forward = [];
    private readonly DirectoryWatcherService _watcher;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _selectionPreviewCts;
    private List<FileEntryViewModel> _loaded = [];
    private int _watcherRefreshPending;
    private string[]? _preserveSelectionPaths;

    private string _currentPath = string.Empty;
    private string _addressText = string.Empty;
    private string _searchQuery = string.Empty;
    private bool _isAddressEditing;
    private bool _isLoading;
    private bool _showHiddenFiles;
    private bool _isDetailsView = true;
    private bool _isEmpty;
    private bool _isSearchMode;
    private string _statusText = Loc.T("StatusPreparing");
    private string _selectionText = string.Empty;
    private string _platformText = string.Empty;
    private string _themeLabel = Loc.T("ThemeSystem");
    private string _themeMode = "system";
    private string _sortColumn = "Name";
    private bool _sortDescending;
    private string _sortIndicator = Loc.T("SortName") + Loc.T("SortAsc");
    private bool _canGoBack;
    private bool _canGoForward;
    private bool _canGoUp;
    private bool _isInfoOpen;
    private string _infoTitle = string.Empty;
    private string _infoMessage = string.Empty;
    private FAInfoBarSeverity _infoSeverity = FAInfoBarSeverity.Informational;
    private FileEntryViewModel? _primarySelection;
    private bool _hasSelection;

    public MainViewModel(UiServices ui)
    {
        _ui = ui;
        _watcher = new DirectoryWatcherService(OnDirectoryChanged);

        GoBackCommand = Async(_ => GoBackAsync());
        GoForwardCommand = Async(_ => GoForwardAsync());
        GoUpCommand = Async(_ => GoUpAsync());
        RefreshCommand = Async(_ => LoadAsync());
        NavigateAddressCommand = Async(p => NavigateAsync(p as string ?? AddressText));
        NavigatePathCommand = Async(p => NavigateAsync(p as string ?? string.Empty));
        NavigateSegmentCommand = Async(p => p is PathSegment s ? NavigateAsync(s.FullPath) : Task.CompletedTask);
        EditAddressCommand = new RelayCommand(_ => IsAddressEditing = !IsAddressEditing);
        OpenCommand = Async(p => OpenEntryAsync(p as FileEntryViewModel));
        OpenSelectedCommand = Async(_ => OpenEntryAsync(PrimarySelection));
        NewFolderCommand = Async(_ => NewFolderAsync());
        RenameCommand = Async(_ => RenameAsync());
        DeleteCommand = Async(_ => DeleteAsync());
        CopyCommand = new RelayCommand(_ => CopyOrCut(ClipboardOperation.Copy));
        CutCommand = new RelayCommand(_ => CopyOrCut(ClipboardOperation.Cut));
        PasteCommand = Async(_ => PasteAsync());
        CopyPathCommand = Async(_ => CopyPathAsync());
        RevealCommand = new RelayCommand(_ => RevealSelected());
        PropertiesCommand = Async(_ => PropertiesAsync());
        SelectAllCommand = new RelayCommand(_ => _ui.SelectAll());
        ToggleHiddenCommand = Async(async _ => { ShowHiddenFiles = !ShowHiddenFiles; await LoadAsync(); });
        ToggleViewCommand = new RelayCommand(_ => IsDetailsView = !IsDetailsView);
        ToggleThemeCommand = new RelayCommand(_ => ToggleTheme());
        SetSortCommand = new RelayCommand(p => SetSort(p as string ?? "Name"));
        DeepSearchCommand = Async(_ => DeepSearchAsync());
        ClearSearchCommand = new RelayCommand(_ => SearchQuery = string.Empty);
        DismissInfoCommand = new RelayCommand(_ => IsInfoOpen = false);
        SelectDetailsTabCommand = new RelayCommand(_ => IsPreviewTab = false);
        SelectPreviewTabCommand = new RelayCommand(_ => IsPreviewTab = true);
        SelectTabCommand = new RelayCommand(_ => Shell?.Activate(this));
        CloseTabCommand = new RelayCommand(_ => Shell?.CloseTab(this));
    }

    public ObservableCollection<FileEntryViewModel> Entries { get; } = [];
    public ObservableCollection<PathSegment> Breadcrumbs { get; } = [];
    public ObservableCollection<LocationEntry> Locations { get; } = [];
    public List<FileEntryViewModel> SelectedEntries { get; } = [];

    public ICommand GoBackCommand { get; }
    public ICommand GoForwardCommand { get; }
    public ICommand GoUpCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand NavigateAddressCommand { get; }
    public ICommand NavigatePathCommand { get; }
    public ICommand NavigateSegmentCommand { get; }
    public ICommand EditAddressCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand OpenSelectedCommand { get; }
    public ICommand NewFolderCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand CutCommand { get; }
    public ICommand PasteCommand { get; }
    public ICommand CopyPathCommand { get; }
    public ICommand RevealCommand { get; }
    public ICommand PropertiesCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand ToggleHiddenCommand { get; }
    public ICommand ToggleViewCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand SetSortCommand { get; }
    public ICommand DeepSearchCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand DismissInfoCommand { get; }
    public ICommand SelectDetailsTabCommand { get; }
    public ICommand SelectPreviewTabCommand { get; }

    /// <summary>Set by ShellViewModel when this tab becomes the active one.</summary>
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
    private bool _isActive;

    public bool IsReady { get; private set; }
    public ShellViewModel? Shell { get; set; }

    /// <summary>Optional folder to open on first initialization (session restore / reopen tab).</summary>
    public string InitialPath { get; set; } = string.Empty;

    public string TabTitle
    {
        get
        {
            if (string.IsNullOrWhiteSpace(CurrentPath)) return Loc.T("TabHome");
            var name = VfsPath.IsVirtualPath(CurrentPath)
                ? VfsPath.Parse(CurrentPath).FileName
                : Path.GetFileName(CurrentPath.TrimEnd(Path.DirectorySeparatorChar));
            return string.IsNullOrEmpty(name) ? CurrentPath : name;
        }
    }

    public ICommand SelectTabCommand { get; }
    public ICommand CloseTabCommand { get; }

    // Proxies so the content area (whose DataContext is the active tab) can still reach shell state.
    public ICommand ToggleDetailsPaneCommand => Shell?.ToggleDetailsPaneCommand
        ?? new RelayCommand(_ => { });
    public bool IsDetailsPaneOpen => Shell?.IsDetailsPaneOpen ?? true;
    public void RaiseDetailsPaneChanged() => OnPropertyChanged(nameof(IsDetailsPaneOpen));

    public string CurrentPath
    {
        get => _currentPath;
        private set
        {
            if (Set(ref _currentPath, value)) OnPropertyChanged(nameof(TabTitle));
        }
    }

    public string AddressText
    {
        get => _addressText;
        set
        {
            if (!Set(ref _addressText, value)) return;
            OnPropertyChanged(nameof(IsAddressUrl));
        }
    }

    /// <summary>True while the address box holds a URL the resolver will take over on Enter.</summary>
    public bool IsAddressUrl => WebLocationService.TryParse(AddressText, out _);

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (Set(ref _searchQuery, value) && !_isSearchMode) ApplyFilterAndSort();
        }
    }

    public bool IsAddressEditing { get => _isAddressEditing; set => Set(ref _isAddressEditing, value); }
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }
    public bool ShowHiddenFiles { get => _showHiddenFiles; private set => Set(ref _showHiddenFiles, value); }

    public bool IsDetailsView
    {
        get => _isDetailsView;
        private set
        {
            if (Set(ref _isDetailsView, value)) OnPropertyChanged(nameof(IsGridView));
        }
    }

    public bool IsGridView => !_isDetailsView;
    public bool IsEmpty { get => _isEmpty; private set => Set(ref _isEmpty, value); }
    public bool IsSearchMode { get => _isSearchMode; private set => Set(ref _isSearchMode, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string SelectionText { get => _selectionText; private set => Set(ref _selectionText, value); }
    public string PlatformText { get => _platformText; private set => Set(ref _platformText, value); }
    public string ThemeLabel { get => _themeLabel; private set => Set(ref _themeLabel, value); }
    public string SortIndicator { get => _sortIndicator; private set => Set(ref _sortIndicator, value); }
    public bool CanGoBack { get => _canGoBack; private set => Set(ref _canGoBack, value); }
    public bool CanGoForward { get => _canGoForward; private set => Set(ref _canGoForward, value); }
    public bool CanGoUp { get => _canGoUp; private set => Set(ref _canGoUp, value); }
    public bool IsInfoOpen { get => _isInfoOpen; set => Set(ref _isInfoOpen, value); }
    public bool IsPreviewTab { get => _isPreviewTab; private set => Set(ref _isPreviewTab, value); }
    private bool _isPreviewTab;
    public string InfoTitle { get => _infoTitle; private set => Set(ref _infoTitle, value); }
    public string InfoMessage { get => _infoMessage; private set => Set(ref _infoMessage, value); }
    public FAInfoBarSeverity InfoSeverity { get => _infoSeverity; private set => Set(ref _infoSeverity, value); }
    public FileEntryViewModel? PrimarySelection { get => _primarySelection; private set => Set(ref _primarySelection, value); }
    public bool HasSelection { get => _hasSelection; private set => Set(ref _hasSelection, value); }

    public void LoadLocations(bool force = false)
    {
        if (Locations.Count > 0 && !force) return;
        Locations.Clear();
        foreach (var location in FileSystemService.GetLocations()) Locations.Add(location);
        foreach (var remote in RemoteLocationStore.Load())
        {
            Locations.Add(new LocationEntry
            {
                Name = remote.Name,
                Path = remote.RootPath,
                IconKey = remote.Protocol switch
                {
                    RemoteProtocol.Smb => "HardDrive",
                    RemoteProtocol.Sftp => "Globe",
                    RemoteProtocol.S3 => "Cloud",
                    _ => "Globe"
                },
                Kind = LocationKind.Drive,
                Detail = $"{remote.Protocol} · {remote.Host}"
            });
        }
        // Browsing inside an archive adds a pin for the container so it is one click away.
        foreach (var archive in RecentArchives())
            Locations.Add(archive);

        // URLs opened from the address bar come back as ordinary locations.
        foreach (var web in WebHistoryStore.Load().Take(6))
        {
            Locations.Add(new LocationEntry
            {
                Name = web.Title,
                Path = web.Url,
                IconKey = "Globe",
                Kind = LocationKind.QuickAccess,
                Detail = "Web"
            });
        }
    }

    private IEnumerable<LocationEntry> RecentArchives()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tab in Shell?.Tabs ?? [])
        {
            // A freshly created tab has no path yet; parsing it would throw.
            if (string.IsNullOrWhiteSpace(tab.CurrentPath)) continue;
            var vp = VfsPath.Parse(tab.CurrentPath);
            if (vp.Scheme != VfsScheme.Archive || !seen.Add(vp.Authority)) continue;
            yield return new LocationEntry
            {
                Name = Path.GetFileName(vp.Authority),
                Path = new VfsPath(VfsScheme.Archive, vp.Authority, "/").Canonical,
                IconKey = "ZipFolder",
                Kind = LocationKind.QuickAccess
            };
        }
    }

    public async Task InitializeAsync()
    {
        LoadLocations();
        PlatformText = $"{PlatformService.PlatformName} | {Loc.T("PlatformText")}";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var target = !string.IsNullOrWhiteSpace(InitialPath) && Directory.Exists(InitialPath)
            ? InitialPath : home;
        await NavigateAsync(target, recordHistory: false);
        InitialPath = string.Empty;
        IsReady = true;
    }

    public async Task NavigateAsync(string? path, bool recordHistory = true)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var trimmed = path.Trim().Trim('"');

        // A URL is resolved before the filesystem sees it. It may be a browsable folder, a
        // single file to download, or an ordinary page that belongs in the browser.
        if (WebLocationService.TryParse(trimmed, out var webUrl))
        {
            await NavigateWebAsync(webUrl, recordHistory).ConfigureAwait(true);
            return;
        }

        string full;
        if (VfsPath.IsVirtualPath(trimmed))
        {
            full = VfsPath.Parse(trimmed).Canonical;
        }
        else
        {
            try
            {
                full = Path.GetFullPath(ExpandHome(trimmed));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                ShowInfo(Loc.T("MsgInvalidPath"), ex.Message, FAInfoBarSeverity.Warning);
                return;
            }
        }

        await NavigateCoreAsync(full, recordHistory).ConfigureAwait(true);
    }

    /// <summary>
    /// Opens what a URL points at: folders are browsed in place, files are pulled into the URL
    /// cache and opened with the matching viewer, pages go to the system browser.
    /// </summary>
    private async Task NavigateWebAsync(string url, bool recordHistory)
    {
        StatusText = Loc.T("StatusWebResolving");
        try
        {
            var location = await WebLocationService.ResolveAsync(url).ConfigureAwait(true);
            switch (location.Kind)
            {
                case WebLocationKind.Folder:
                    WebHistoryStore.Record(location.BrowsablePath);
                    LoadLocations(force: true);
                    await NavigateCoreAsync(location.BrowsablePath, recordHistory).ConfigureAwait(true);
                    break;

                case WebLocationKind.File:
                    await DownloadAndOpenAsync(location).ConfigureAwait(true);
                    break;

                default:
                    OpenInBrowser(location.Url);
                    ShowInfo(Loc.T("MsgWebPageTitle"), Loc.F("MsgWebPageBody", location.Url),
                        FAInfoBarSeverity.Informational);
                    StatusText = Loc.F("StatusItems", Entries.Count);
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or NotSupportedException or TimeoutException or ArgumentException
                                       or System.Net.Http.HttpRequestException)
        {
            ShowInfo(Loc.T("MsgWebFailed"), ex.Message, FAInfoBarSeverity.Error);
            StatusText = Loc.F("StatusItems", Entries.Count);
        }
    }

    private async Task DownloadAndOpenAsync(WebLocation location)
    {
        var name = location.SuggestedName ?? location.Url;
        StatusText = Loc.F("StatusWebDownloading", name);
        var progress = new Progress<double>(fraction =>
            StatusText = Loc.F("StatusWebProgress", (int)Math.Round(fraction * 100)));

        var local = await WebCacheService.DownloadAsync(location.Url, progress).ConfigureAwait(true);
        WebHistoryStore.Record(location.Url, name);
        LoadLocations(force: true);
        StatusText = Loc.F("StatusItems", Entries.Count);
        ShowInfo(Loc.T("MsgWebDownloaded"), local, FAInfoBarSeverity.Success);

        try
        {
            await _ui.OpenFileAsync(local).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ShowInfo(Loc.T("MsgCannotOpen"), ex.Message, FAInfoBarSeverity.Warning);
        }
    }

    private static void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No default browser: the info bar already names the URL.
        }
    }

    private async Task NavigateCoreAsync(string full, bool recordHistory)
    {
        if (!await VfsFileSystem.Default.DirectoryExistsAsync(full))
        {
            ShowInfo(Loc.T("MsgCannotOpenLocation"), Loc.F("MsgLocationDetail", full), FAInfoBarSeverity.Warning);
            return;
        }

        if (recordHistory && !string.IsNullOrEmpty(_currentPath) &&
            !string.Equals(full, _currentPath, StringComparison.OrdinalIgnoreCase))
        {
            _back.Add(_currentPath);
            _forward.Clear();
        }

        CurrentPath = full;
        AddressText = full;
        IsAddressEditing = false;
        IsSearchMode = false;
        _watcher.Watch(full);
        BuildBreadcrumbs(full);
        UpdateNavState();
        await LoadAsync();
    }

    public Task RefreshAsync() => LoadAsync();

    private static string ExpandHome(string path)
    {
        if (path == "~") return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        return path;
    }

    private void BuildBreadcrumbs(string path)
    {
        Breadcrumbs.Clear();
        if (VfsPath.IsVirtualPath(path))
        {
            // Archive: "<container>.zip / docs / readme.md". Remote: "smb://host / share / dir".
            foreach (var (name, segmentPath) in VfsPath.Parse(path).Segments())
                Breadcrumbs.Add(new PathSegment { Name = name, FullPath = segmentPath });
            return;
        }
        var chain = new Stack<DirectoryInfo>();
        DirectoryInfo? current;
        try { current = new DirectoryInfo(path); }
        catch { return; }
        while (current is not null)
        {
            chain.Push(current);
            current = current.Parent;
        }
        foreach (var dir in chain)
        {
            // The filesystem root has no parent. Its Name is the full path ("/" on macOS,
            // "C:\" on Windows), so show a volume label instead — this is what Files App shows.
            var label = dir.Parent is null ? RootLabel(dir) : dir.Name;
            Breadcrumbs.Add(new PathSegment { Name = label, FullPath = dir.FullName });
        }
    }

    private static string RootLabel(DirectoryInfo dir)
    {
        if (OperatingSystem.IsMacOS()) return "Macintosh HD";
        if (OperatingSystem.IsWindows())
        {
            var letter = dir.FullName.TrimEnd(Path.DirectorySeparatorChar);
            return string.IsNullOrEmpty(letter) ? "Local Disk" : letter;
        }
        return dir.FullName;
    }

    /// <summary>
    /// Called by the directory watcher on a background thread. Coalesced there already; here we
    /// marshal to the UI thread, skip search results (a directory refresh would overwrite them)
    /// and keep the user's current selection by path.
    /// </summary>
    private void OnDirectoryChanged()
    {
        if (_isSearchMode) return;
        Dispatcher.UIThread.Post(async () =>
        {
            if (Interlocked.Exchange(ref _watcherRefreshPending, 1) != 0) return;
            try
            {
                if (_isSearchMode || string.IsNullOrEmpty(CurrentPath)) return;
                await LoadAsync(preserveSelection: true);
            }
            catch (Exception ex)
            {
                ShowInfo(Loc.T("MsgReadFailed"), ex.Message, FAInfoBarSeverity.Warning);
            }
            finally
            {
                Interlocked.Exchange(ref _watcherRefreshPending, 0);
            }
        });
    }

    private async Task LoadAsync(bool preserveSelection = false)
    {
        _preserveSelectionPaths = preserveSelection
            ? SelectedEntries.Select(x => x.FullPath).ToArray()
            : null;
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;
        IsLoading = true;
        try
        {
            var items = await FileSystemService.ListDirectoryAsync(CurrentPath, ShowHiddenFiles, token);
            if (token.IsCancellationRequested) return;
            _loaded = items.Select(x => new FileEntryViewModel(x)).ToList();
            ApplyFilterAndSort();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowInfo(Loc.T("MsgReadFailed"), ex.Message, FAInfoBarSeverity.Error);
        }
        finally
        {
            if (!token.IsCancellationRequested) IsLoading = false;
        }
    }

    private void ApplyFilterAndSort()
    {
        var query = SearchQuery.Trim();
        IEnumerable<FileEntryViewModel> source = _loaded;
        if (!_isSearchMode && query.Length > 0)
            source = source.Where(x => x.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

        var list = source.ToList();
        list.Sort((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
            var result = _sortColumn switch
            {
                "Size" => a.Model.Size.CompareTo(b.Model.Size),
                "Modified" => a.Model.LastWriteTime.CompareTo(b.Model.LastWriteTime),
                "Kind" => string.Compare(a.Kind, b.Kind, StringComparison.OrdinalIgnoreCase),
                _ => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
            };
            return _sortDescending ? -result : result;
        });

        Entries.Clear();
        foreach (var item in list) Entries.Add(item);
        IsEmpty = list.Count == 0;
        StatusText = _isSearchMode
            ? Loc.F("StatusSearchResults", list.Count)
            : query.Length > 0
                ? Loc.F("StatusFiltered", list.Count, _loaded.Count)
                : Loc.F("StatusItems", list.Count);
        // A watcher refresh rebuilds the whole list. Re-select by full path so external
        // changes do not silently drop the user's current selection.
        var restore = _preserveSelectionPaths;
        _preserveSelectionPaths = null;
        if (restore is { Length: > 0 })
            SetSelection(list.Where(x => restore.Contains(x.FullPath, StringComparer.OrdinalIgnoreCase)));
        else
            SetSelection([]);
        RequestListPreviews();
    }

    /// <summary>
    /// Files App generates thumbnails for visible items lazily. Avalonia's ListBox virtualises,
    /// so requesting for every image would decode files the user never scrolls to. A bounded
    /// first page keeps the common case (a folder of photos) responsive; the pane request on
    /// selection covers everything else on demand.
    /// </summary>
    private void RequestListPreviews()
    {
        var token = _loadCts?.Token ?? default;
        var images = Entries.Where(x => x.PreviewKind == PreviewKind.Image).Take(80);
        foreach (var entry in images)
            _ = entry.EnsurePreviewAsync(ThumbnailService.ListWidth, token);
    }

    private async Task DeepSearchAsync()
    {
        var query = SearchQuery.Trim();
        if (query.Length == 0)
        {
            ShowInfo(Loc.T("MsgSearch"), Loc.T("MsgSearchEmpty"), FAInfoBarSeverity.Informational);
            return;
        }

        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;
        IsLoading = true;
        try
        {
            var items = await FileSystemService.SearchAsync(CurrentPath, query, 300, token);
            if (token.IsCancellationRequested) return;
            _isSearchMode = true;
            OnPropertyChanged(nameof(IsSearchMode));
            _loaded = items.Select(x => new FileEntryViewModel(x)).ToList();
            ApplyFilterAndSort();
            Breadcrumbs.Clear();
            Breadcrumbs.Add(new PathSegment { Name = Loc.F("MsgSearchBreadcrumb", query), FullPath = CurrentPath });
            ShowInfo(Loc.T("MsgSearchDone"), Loc.F("MsgSearchDoneBody", _loaded.Count, CurrentPath), FAInfoBarSeverity.Success);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowInfo(Loc.T("MsgSearchFailed"), ex.Message, FAInfoBarSeverity.Error); }
        finally { if (!token.IsCancellationRequested) IsLoading = false; }
    }

    private async Task GoBackAsync()
    {
        if (_back.Count == 0) return;
        var target = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        _forward.Add(CurrentPath);
        await NavigateAsync(target, recordHistory: false);
    }

    private async Task GoForwardAsync()
    {
        if (_forward.Count == 0) return;
        var target = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        _back.Add(CurrentPath);
        await NavigateAsync(target, recordHistory: false);
    }

    private Task GoUpAsync()
    {
        var virtualPath = VfsPath.Parse(CurrentPath);
        if (virtualPath.IsVirtual)
        {
            if (virtualPath.Parent is { } parent) return NavigateAsync(parent.Canonical);
            // Leaving an archive root returns to the folder that holds the archive.
            if (virtualPath.Scheme == VfsScheme.Archive)
                return NavigateAsync(Path.GetDirectoryName(virtualPath.Authority) ?? "/");
            return Task.CompletedTask;
        }

        var parentDir = Directory.GetParent(CurrentPath);
        return parentDir is null ? Task.CompletedTask : NavigateAsync(parentDir.FullName);
    }

    private void UpdateNavState()
    {
        CanGoBack = _back.Count > 0;
        CanGoForward = _forward.Count > 0;
        var virtualPath = VfsPath.Parse(CurrentPath);
        CanGoUp = virtualPath.IsVirtual
            ? virtualPath.Parent is not null || virtualPath.Scheme == VfsScheme.Archive
            : Directory.GetParent(CurrentPath) is not null;
    }

    private async Task OpenEntryAsync(FileEntryViewModel? entry)
    {
        if (entry is null) return;
        if (entry.IsDirectory)
        {
            await NavigateAsync(entry.FullPath);
            return;
        }

        // Archives open as folders. A remote archive would need provider-to-provider streaming,
        // which the archive provider does not support yet, so those fall through to "open".
        if (!VfsPath.IsVirtualPath(entry.FullPath) && ArchiveService.IsArchiveExtension(entry.FullPath))
        {
            var root = new VfsPath(VfsScheme.Archive, entry.FullPath, "/").Canonical;
            if (await VfsFileSystem.Default.DirectoryExistsAsync(root))
            {
                await NavigateAsync(root);
                return;
            }
        }

        try
        {
            await _ui.OpenFileAsync(entry.FullPath);
        }
        catch (Exception ex)
        {
            ShowInfo(Loc.T("MsgCannotOpen"), ex.Message, FAInfoBarSeverity.Error);
        }
    }

    private async Task NewFolderAsync()
    {
        var name = await _ui.PromptAsync(Loc.T("DlgNewFolderTitle"), Loc.F("DlgNewFolderMessage", CurrentPath), Loc.T("DlgNewFolderDefault"), Loc.T("DlgCreate"));
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var created = await FileSystemService.CreateFolderAsync(CurrentPath, name.Trim());
            Shell?.Undo.Push(new CreateFolderOperation(created,
                (path, ct) => VfsFileSystem.Default.DeleteAsync(path, recursive: false, ct),
                (path, ct) =>
                {
                    var vp = VfsPath.Parse(path).Parent;
                    return vp is null
                        ? Task.CompletedTask
                        : VfsFileSystem.Default.CreateFolderAsync(vp.Value.Canonical, VfsPath.Parse(path).FileName, ct);
                }));
            await LoadAsync();
            ShowInfo(Loc.T("MsgCreatedFolder"), VfsPath.Parse(created).FileName, FAInfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowInfo(Loc.T("MsgCreateFailed"), ex.Message, FAInfoBarSeverity.Error); }
    }

    private async Task RenameAsync()
    {
        var target = PrimarySelection;
        if (target is null) return;
        var name = await _ui.PromptAsync(Loc.T("DlgRenameTitle"), Loc.F("DlgRenameMessage", target.Name), target.Name, Loc.T("DlgRenameButton"));
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var before = target.FullPath;
            var after = await FileSystemService.RenameAsync(before, name.Trim());
            if (!target.Model.IsVirtual) ThumbnailService.Instance.Invalidate(before);
            Shell?.Undo.Push(new RenameOperation(before, after,
                (source, newName, ct) => VfsFileSystem.Default.RenameAsync(source, newName, ct)));
            await LoadAsync();
            ShowInfo(Loc.T("MsgRenameDone"), name.Trim(), FAInfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowInfo(Loc.T("MsgRenameFailed"), ex.Message, FAInfoBarSeverity.Error); }
    }

    private async Task DeleteAsync()
    {
        var targets = SelectedEntries.Select(x => x.FullPath).ToArray();
        if (targets.Length == 0) return;
        var ok = await _ui.ConfirmAsync(Loc.T("DlgDeleteTitle"),
            Loc.F("DlgDeleteMessage", targets.Length,
                Path.GetFileName(targets[0]) + (targets.Length > 1 ? " ..." : "")));
        if (!ok) return;

        var done = 0;
        var errors = new List<string>();
        foreach (var path in targets)
        {
            try
            {
                if (VfsPath.IsVirtualPath(path))
                {
                    // Nothing to recycle outside the local disk: providers delete directly.
                    await VfsFileSystem.Default.DeleteAsync(path);
                }
                else
                {
                    ThumbnailService.Instance.Invalidate(path);
                    RecycleBinService.MoveToTrash(path);
                }
                done++;
            }
            catch (Exception ex) { errors.Add($"{VfsPath.Parse(path).FileName}: {ex.Message}"); }
        }
        await LoadAsync();
        ShowInfo(done > 0 ? Loc.T("MsgMovedToTrash") : Loc.T("MsgDeleteFailed"),
            errors.Count == 0 ? Loc.F("StatusItems", done) : string.Join("\n", errors.Take(3)),
            errors.Count == 0 ? FAInfoBarSeverity.Success : FAInfoBarSeverity.Warning);
    }

    private void CopyOrCut(ClipboardOperation operation)
    {
        if (SelectedEntries.Count == 0) return;
        _clipboard.Set(operation, SelectedEntries.Select(x => x.FullPath), CurrentPath);
        ShowInfo(operation == ClipboardOperation.Copy ? Loc.T("MsgCopied") : Loc.T("MsgCut"),
            Loc.F("MsgClipboardHint", SelectedEntries.Count),
            FAInfoBarSeverity.Informational);
    }

    private async Task PasteAsync()
    {
        if (!_clipboard.HasContent) return;
        var operation = _clipboard.Operation;
        var paths = _clipboard.Paths.ToArray();
        IsLoading = true;
        try
        {
            var result = operation == ClipboardOperation.Copy
                ? await FileSystemService.CopyIntoAsync(paths, CurrentPath)
                : await FileSystemService.MoveIntoAsync(paths, CurrentPath);
            _clipboard.Clear();
            await LoadAsync();
            if (result.Errors.Count > 0)
            {
                ShowInfo(Loc.T("MsgPasteFailed"), string.Join("\n", result.Errors.Take(3)), FAInfoBarSeverity.Warning);
                return;
            }
            ShowInfo(Loc.T("MsgPasteDone"), Loc.F("MsgPasteBody", result.Succeeded,
                operation == ClipboardOperation.Copy ? Loc.T("MsgCopiedWord") : Loc.T("MsgMovedWord")),
                FAInfoBarSeverity.Success);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowInfo(Loc.T("MsgPasteFailed"), ex.Message, FAInfoBarSeverity.Error); }
        finally { IsLoading = false; }
    }

    /// <summary>
    /// Handles files dropped onto a folder / the current directory. Destination is validated
    /// against dropping a folder into its own subtree; per-item errors are collected so a single
    /// locked file does not cancel the rest of the batch.
    /// </summary>
    public async Task DropIntoAsync(IEnumerable<string> sources, string destination, bool copy)
    {
        var paths = sources.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0 || string.IsNullOrWhiteSpace(destination)) return;

        var invalid = paths.FirstOrDefault(p => FileSystemService.IsDescendantOrSelf(p, destination));
        if (invalid is not null)
        {
            ShowInfo(Loc.T("MsgDropInvalid"), Path.GetFileName(invalid), FAInfoBarSeverity.Warning);
            return;
        }

        if (!await VfsFileSystem.Default.DirectoryExistsAsync(destination))
        {
            ShowInfo(Loc.T("MsgDropFailed"), Loc.F("MsgLocationDetail", destination), FAInfoBarSeverity.Warning);
            return;
        }

        var token = _loadCts?.Token ?? default;
        IsLoading = true;
        var result = copy
            ? await FileSystemService.CopyIntoAsync(paths, destination, null, token)
            : await FileSystemService.MoveIntoAsync(paths, destination, null, token);
        IsLoading = false;

        foreach (var path in paths)
        {
            if (!VfsPath.IsVirtualPath(path)) ThumbnailService.Instance.Invalidate(path);
        }
        await LoadAsync(preserveSelection: true);

        if (result.Errors.Count > 0)
            ShowInfo(Loc.T("MsgDropFailed"), string.Join("\n", result.Errors.Take(3)), FAInfoBarSeverity.Warning);
        else
            ShowInfo(copy ? Loc.T("MsgDropCopied") : Loc.T("MsgDropDone"),
                Loc.F("MsgDropBody", result.Succeeded, destination), FAInfoBarSeverity.Success);
    }

    private async Task CopyPathAsync()
    {
        if (PrimarySelection is null) return;
        await _ui.CopyTextAsync(PrimarySelection.FullPath);
        ShowInfo(Loc.T("MsgPathCopied"), PrimarySelection.FullPath, FAInfoBarSeverity.Success);
    }

    private void RevealSelected()
    {
        if (PrimarySelection is null) return;
        try { _ui.Reveal(PrimarySelection.FullPath); }
        catch (Exception ex) { ShowInfo(Loc.T("MsgCannotReveal"), ex.Message, FAInfoBarSeverity.Error); }
    }

    private async Task PropertiesAsync()
    {
        var item = PrimarySelection;
        if (item is null) return;
        var info = item.IsDirectory
            ? Loc.F("PropFolder", item.FullPath, item.ModifiedText, CountChildren(item.FullPath))
            : Loc.F("PropFile", item.Kind, item.FullPath, item.SizeText, item.Model.Size, item.ModifiedText);
        await _ui.ShowInfoAsync(Loc.T("DlgPropertiesTitle"), info);
    }

    private static string CountChildren(string path)
    {
        try { return Directory.EnumerateFileSystemEntries(path).Take(1000).Count().ToString(); }
        catch { return Loc.T("Unknown"); }
    }

    private void SetSort(string column)
    {
        if (_sortColumn == column) _sortDescending = !_sortDescending;
        else { _sortColumn = column; _sortDescending = false; }
        var label = column switch { "Size" => Loc.T("SortSize"), "Modified" => Loc.T("SortModified"), "Kind" => Loc.T("SortKind"), _ => Loc.T("SortName") };
        SortIndicator = label + (_sortDescending ? Loc.T("SortDesc") : Loc.T("SortAsc"));
        ApplyFilterAndSort();
    }

    private void ToggleTheme()
    {
        _themeMode = _themeMode switch { "system" => "light", "light" => "dark", _ => "system" };
        ThemeLabel = _themeMode switch { "light" => Loc.T("ThemeLight"), "dark" => Loc.T("ThemeDark"), _ => Loc.T("ThemeSystem") };
        _ui.SetTheme(_themeMode);
    }

    public void SetSelection(IEnumerable<FileEntryViewModel> items)
    {
        SelectedEntries.Clear();
        SelectedEntries.AddRange(items);
        PrimarySelection = SelectedEntries.FirstOrDefault();
        HasSelection = SelectedEntries.Count > 0;
        var bytes = SelectedEntries.Where(x => !x.IsDirectory).Sum(x => x.Model.Size);
        SelectionText = HasSelection
            ? Loc.F("SelectionText", SelectedEntries.Count, FileSystemService.FormatSize(bytes))
            : string.Empty;

        // Files' preview pane reacts to selection: decode the large image or read the text
        // prefix asynchronously so a big file never blocks the UI thread.
        _selectionPreviewCts?.Cancel();
        _selectionPreviewCts = new CancellationTokenSource();
        if (PrimarySelection is { } selected && selected.PreviewKind != PreviewKind.None)
            _ = selected.EnsurePreviewAsync(ThumbnailService.PaneWidth, _selectionPreviewCts.Token);
    }

    internal void ShowInfo(string title, string message, FAInfoBarSeverity severity)
    {
        InfoTitle = title;
        InfoMessage = message;
        InfoSeverity = severity;
        IsInfoOpen = true;
    }

    private AsyncRelayCommand Async(Func<object?, Task> execute)
        => new(execute) { ErrorHandler = ex => ShowInfo(Loc.T("MsgOperationFailed"), ex.Message, FAInfoBarSeverity.Error) };

    // ---- Archive, batch rename and remote locations -------------------------------------
    // These live here rather than in a separate coordinator because they all act on the
    // current folder/selection of one tab.

    /// <summary>Opens the batch rename dialog for the current selection and applies the plan.</summary>
    public async Task OpenBatchRenameAsync()
    {
        if (SelectedEntries.Count == 0)
        {
            ShowInfo(Loc.T("DlgRenameTitle"), Loc.T("MsgSearchEmpty"), FAInfoBarSeverity.Informational);
            return;
        }

        var paths = SelectedEntries.Select(x => x.FullPath).ToArray();
        var plan = await BatchRenameDialog.ShowAsync(paths).ConfigureAwait(true);
        if (plan is null) return;

        var applied = await BatchRenameService.ApplyAsync(
            plan, (source, newName) => VfsFileSystem.Default.RenameAsync(source, newName)).ConfigureAwait(true);

        var moves = plan.Items
            .Where(x => x.IsValid && x.Error is null && !string.Equals(x.TargetName, x.SourceName, StringComparison.Ordinal))
            .Select(x => new RenameRecord(x.SourcePath, x.TargetPath))
            .ToList();
        if (moves.Count > 0 && applied > 0)
        {
            Shell?.Undo.Push(new RenameOperation($"Rename {moves.Count} items", moves,
                (source, newName, ct) => VfsFileSystem.Default.RenameAsync(source, newName, ct)));
        }

        await LoadAsync().ConfigureAwait(true);
        var failed = plan.Items.Count(x => !x.IsValid || x.Error is not null);
        ShowInfo(Loc.T("MsgRenameDone"), $"{applied} / {plan.Items.Count}",
            failed == 0 ? FAInfoBarSeverity.Success : FAInfoBarSeverity.Warning);
    }

    /// <summary>Compresses the selection (or the current folder) into a .zip next to the target.</summary>
    public async Task CompressSelectionAsync()
    {
        var sources = SelectedEntries.Count > 0
            ? SelectedEntries.Select(x => x.FullPath).ToArray()
            : [CurrentPath];

        var suggested = sources.Length == 1
            ? VfsPath.Parse(sources[0]).FileName + ".zip"
            : "Archive.zip";
        var name = await _ui.PromptAsync("Compress",
            $"{sources.Length} item(s) -> {CurrentPath}", suggested, "Compress").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name)) return;

        var target = VfsPath.Parse(CurrentPath).Combine(name.Trim()).Canonical;
        if (VfsPath.IsVirtualPath(CurrentPath))
        {
            ShowInfo("Compress", "Compressing into a virtual location is not supported yet.",
                FAInfoBarSeverity.Warning);
            return;
        }

        IsLoading = true;
        try
        {
            await ArchiveService.CreateAsync(sources, target,
                ArchiveService.GuessFormat(target) ?? ArchiveFormat.Zip, null, CancellationToken.None)
                .ConfigureAwait(true);
            await LoadAsync().ConfigureAwait(true);
            ShowInfo("Compress", VfsPath.Parse(target).FileName, FAInfoBarSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ShowInfo("Compress failed", ex.Message, FAInfoBarSeverity.Error);
        }
        finally { IsLoading = false; }
    }

    /// <summary>
    /// Extracts the archive being browsed, or the selected archive file, into a sibling folder.
    /// </summary>
    public async Task ExtractArchiveAsync()
    {
        string archiveFile;
        string innerPath;
        string destination;
        var current = VfsPath.Parse(CurrentPath);

        if (current.Scheme == VfsScheme.Archive)
        {
            archiveFile = current.Authority;
            innerPath = current.Inner;
            destination = Path.Combine(Path.GetDirectoryName(archiveFile) ?? CurrentPath,
                Path.GetFileNameWithoutExtension(archiveFile));
        }
        else if (PrimarySelection is { IsDirectory: false } selected &&
                 ArchiveService.IsArchiveExtension(selected.FullPath))
        {
            archiveFile = selected.FullPath;
            innerPath = "/";
            destination = Path.Combine(Path.GetDirectoryName(archiveFile) ?? CurrentPath,
                Path.GetFileNameWithoutExtension(archiveFile));
        }
        else
        {
            ShowInfo("Extract", "Select an archive, or open one first.", FAInfoBarSeverity.Informational);
            return;
        }

        IsLoading = true;
        try
        {
            var count = await ArchiveService.ExtractAsync(archiveFile, innerPath, destination, null, null,
                CancellationToken.None).ConfigureAwait(true);
            ShowInfo("Extract", $"{count} file(s) -> {destination}", FAInfoBarSeverity.Success);
            await NavigateAsync(Path.GetDirectoryName(destination) ?? CurrentPath).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ShowInfo("Extract failed", ex.Message, FAInfoBarSeverity.Error);
        }
        finally { IsLoading = false; }
    }

    /// <summary>Adds a network location and navigates to it.</summary>
    public async Task AddRemoteLocationAsync()
    {
        var location = await RemoteLocationDialog.ShowAsync().ConfigureAwait(true);
        if (location is null) return;

        var all = RemoteLocationStore.Load().Where(x => x.Id != location.Id).Append(location).ToList();
        RemoteLocationStore.Save(all);
        LoadLocations(force: true);

        await NavigateAsync(location.RootPath).ConfigureAwait(true);
    }
}
