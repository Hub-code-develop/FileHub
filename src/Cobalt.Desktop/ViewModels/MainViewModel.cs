using System.Collections.ObjectModel;
using System.Windows.Input;
using Cobalt.Desktop.Models;
using Cobalt.Files.Models;
using Cobalt.Files.Services;
using FluentAvalonia.UI.Controls;

namespace Cobalt.Desktop.ViewModels;

/// <summary>UI 侧能力注入，ViewModel 不直接依赖 Window / TopLevel。</summary>
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
    private CancellationTokenSource? _loadCts;
    private List<FileEntryViewModel> _loaded = [];

    private string _currentPath = string.Empty;
    private string _addressText = string.Empty;
    private string _searchQuery = string.Empty;
    private bool _isAddressEditing;
    private bool _isLoading;
    private bool _showHiddenFiles;
    private bool _isDetailsView = true;
    private bool _isEmpty;
    private bool _isSearchMode;
    private string _statusText = "正在准备…";
    private string _selectionText = string.Empty;
    private string _platformText = string.Empty;
    private string _themeLabel = "跟随系统";
    private string _themeMode = "system";
    private string _sortColumn = "Name";
    private bool _sortDescending;
    private string _sortIndicator = "名称 ↑";
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

    public string CurrentPath { get => _currentPath; private set => Set(ref _currentPath, value); }

    public string AddressText { get => _addressText; set => Set(ref _addressText, value); }

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
    public string InfoTitle { get => _infoTitle; private set => Set(ref _infoTitle, value); }
    public string InfoMessage { get => _infoMessage; private set => Set(ref _infoMessage, value); }
    public FAInfoBarSeverity InfoSeverity { get => _infoSeverity; private set => Set(ref _infoSeverity, value); }
    public FileEntryViewModel? PrimarySelection { get => _primarySelection; private set => Set(ref _primarySelection, value); }
    public bool HasSelection { get => _hasSelection; private set => Set(ref _hasSelection, value); }

    public void LoadLocations()
    {
        if (Locations.Count > 0) return;
        foreach (var location in FileSystemService.GetLocations()) Locations.Add(location);
    }

    public async Task InitializeAsync()
    {
        LoadLocations();
        PlatformText = $"{PlatformService.PlatformName} · .NET 10 · Avalonia 12 · FluentUI-CodeHub";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        await NavigateAsync(home, recordHistory: false);
    }

    public async Task NavigateAsync(string? path, bool recordHistory = true)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string full;
        try
        {
            full = Path.GetFullPath(ExpandHome(path.Trim().Trim('"')));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ShowInfo("路径无效", ex.Message, FAInfoBarSeverity.Warning);
            return;
        }

        if (!Directory.Exists(full))
        {
            ShowInfo("打不开这个位置", $"目录不存在或没有权限：{full}", FAInfoBarSeverity.Warning);
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
            var label = string.IsNullOrEmpty(dir.Name) ? dir.FullName : dir.Name;
            Breadcrumbs.Add(new PathSegment { Name = label, FullPath = dir.FullName });
        }
    }

    private async Task LoadAsync()
    {
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
            ShowInfo("读取目录失败", ex.Message, FAInfoBarSeverity.Error);
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
            ? $"搜索结果 {list.Count} 项（上限 300）"
            : query.Length > 0
                ? $"筛选出 {list.Count} 项 / 共 {_loaded.Count} 项"
                : $"{list.Count} 个项目";
        SetSelection([]);
    }

    private async Task DeepSearchAsync()
    {
        var query = SearchQuery.Trim();
        if (query.Length == 0)
        {
            ShowInfo("搜索", "先输入要查找的名称", FAInfoBarSeverity.Informational);
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
            Breadcrumbs.Add(new PathSegment { Name = $"搜索 “{query}”", FullPath = CurrentPath });
            ShowInfo("搜索完成", $"在 {CurrentPath} 下找到 {_loaded.Count} 项", FAInfoBarSeverity.Success);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowInfo("搜索失败", ex.Message, FAInfoBarSeverity.Error); }
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
        var parent = Directory.GetParent(CurrentPath);
        return parent is null ? Task.CompletedTask : NavigateAsync(parent.FullName);
    }

    private void UpdateNavState()
    {
        CanGoBack = _back.Count > 0;
        CanGoForward = _forward.Count > 0;
        CanGoUp = Directory.GetParent(CurrentPath) is not null;
    }

    private async Task OpenEntryAsync(FileEntryViewModel? entry)
    {
        if (entry is null) return;
        if (entry.IsDirectory)
        {
            await NavigateAsync(entry.FullPath);
            return;
        }
        try
        {
            await _ui.OpenFileAsync(entry.FullPath);
        }
        catch (Exception ex)
        {
            ShowInfo("无法打开", ex.Message, FAInfoBarSeverity.Error);
        }
    }

    private async Task NewFolderAsync()
    {
        var name = await _ui.PromptAsync("新建文件夹", $"将在 {CurrentPath} 创建", "新建文件夹", "创建");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var created = FileSystemService.CreateFolder(CurrentPath, name.Trim());
            await LoadAsync();
            ShowInfo("已创建文件夹", Path.GetFileName(created), FAInfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowInfo("创建失败", ex.Message, FAInfoBarSeverity.Error); }
    }

    private async Task RenameAsync()
    {
        var target = PrimarySelection;
        if (target is null) return;
        var name = await _ui.PromptAsync("重命名", $"当前名称：{target.Name}", target.Name, "重命名");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            FileSystemService.Rename(target.FullPath, name.Trim());
            await LoadAsync();
            ShowInfo("重命名完成", name.Trim(), FAInfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowInfo("重命名失败", ex.Message, FAInfoBarSeverity.Error); }
    }

    private async Task DeleteAsync()
    {
        var targets = SelectedEntries.Select(x => x.FullPath).ToArray();
        if (targets.Length == 0) return;
        var ok = await _ui.ConfirmAsync("移到回收站",
            $"确定要把这 {targets.Length} 个项目移到回收站吗？\n\n{targets[0]}{(targets.Length > 1 ? " …" : "")}");
        if (!ok) return;

        var done = 0;
        var errors = new List<string>();
        foreach (var path in targets)
        {
            try { RecycleBinService.MoveToTrash(path); done++; }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
        }
        await LoadAsync();
        ShowInfo(done > 0 ? "已移到回收站" : "删除失败",
            errors.Count == 0 ? $"{done} 个项目" : string.Join("\n", errors.Take(3)),
            errors.Count == 0 ? FAInfoBarSeverity.Success : FAInfoBarSeverity.Warning);
    }

    private void CopyOrCut(ClipboardOperation operation)
    {
        if (SelectedEntries.Count == 0) return;
        _clipboard.Set(operation, SelectedEntries.Select(x => x.FullPath), CurrentPath);
        ShowInfo(operation == ClipboardOperation.Copy ? "已复制" : "已剪切",
            $"{SelectedEntries.Count} 个项目 · 到目标文件夹后按粘贴",
            FAInfoBarSeverity.Informational);
    }

    private async Task PasteAsync()
    {
        if (!_clipboard.HasContent) return;
        var operation = _clipboard.Operation;
        var paths = _clipboard.Paths.ToArray();
        try
        {
            await Task.Run(() =>
            {
                if (operation == ClipboardOperation.Copy) FileSystemService.CopyInto(paths, CurrentPath);
                else FileSystemService.MoveInto(paths, CurrentPath);
            });
            _clipboard.Clear();
            await LoadAsync();
            ShowInfo("粘贴完成", $"{paths.Length} 个项目已{(operation == ClipboardOperation.Copy ? "复制" : "移动")}到当前目录",
                FAInfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowInfo("粘贴失败", ex.Message, FAInfoBarSeverity.Error); }
    }

    private async Task CopyPathAsync()
    {
        if (PrimarySelection is null) return;
        await _ui.CopyTextAsync(PrimarySelection.FullPath);
        ShowInfo("路径已复制", PrimarySelection.FullPath, FAInfoBarSeverity.Success);
    }

    private void RevealSelected()
    {
        if (PrimarySelection is null) return;
        try { _ui.Reveal(PrimarySelection.FullPath); }
        catch (Exception ex) { ShowInfo("无法显示", ex.Message, FAInfoBarSeverity.Error); }
    }

    private async Task PropertiesAsync()
    {
        var item = PrimarySelection;
        if (item is null) return;
        var info = item.IsDirectory
            ? $"类型：文件夹\n路径：{item.FullPath}\n修改时间：{item.ModifiedText}\n包含：{CountChildren(item.FullPath)} 个项目"
            : $"类型：{item.Kind}\n路径：{item.FullPath}\n大小：{item.SizeText}（{item.Model.Size} 字节）\n修改时间：{item.ModifiedText}";
        await _ui.ShowInfoAsync("属性", info);
    }

    private static string CountChildren(string path)
    {
        try { return Directory.EnumerateFileSystemEntries(path).Take(1000).Count().ToString(); }
        catch { return "未知"; }
    }

    private void SetSort(string column)
    {
        if (_sortColumn == column) _sortDescending = !_sortDescending;
        else { _sortColumn = column; _sortDescending = false; }
        var label = column switch { "Size" => "大小", "Modified" => "修改时间", "Kind" => "类型", _ => "名称" };
        SortIndicator = $"{label} {(_sortDescending ? "↓" : "↑")}";
        ApplyFilterAndSort();
    }

    private void ToggleTheme()
    {
        _themeMode = _themeMode switch { "system" => "light", "light" => "dark", _ => "system" };
        ThemeLabel = _themeMode switch { "light" => "浅色", "dark" => "深色", _ => "跟随系统" };
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
            ? $"已选 {SelectedEntries.Count} 项 · {FileSystemService.FormatSize(bytes)}"
            : string.Empty;
    }

    private void ShowInfo(string title, string message, FAInfoBarSeverity severity)
    {
        InfoTitle = title;
        InfoMessage = message;
        InfoSeverity = severity;
        IsInfoOpen = true;
    }

    private AsyncRelayCommand Async(Func<object?, Task> execute)
        => new(execute) { ErrorHandler = ex => ShowInfo("操作失败", ex.Message, FAInfoBarSeverity.Error) };
}
