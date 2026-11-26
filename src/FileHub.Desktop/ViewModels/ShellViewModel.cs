using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using FileHub.Code.Services;
using FileHub.Desktop.Actions;
using FileHub.Desktop.Services;
using FileHub.Desktop.Views;
using FileHub.Files.Services;

namespace FileHub.Desktop.ViewModels;

/// <summary>Which full-area page is shown on top of the Files workspace.</summary>
public enum WorkspacePageKind
{
    Files,
    Code,
    Extensions
}

/// <summary>
/// Window-level state, modelled after the tab strip in Files App: every tab owns a complete
/// MainViewModel (its own path, history, selection and layout), and the shell only switches
/// between them. Kept as a singleton so rebuilding the window on a language switch keeps all tabs.
/// </summary>
public sealed class ShellViewModel : ViewModelBase
{
    private static ShellViewModel? _current;
    public static ShellViewModel Current => _current ??= new ShellViewModel();

    private MainViewModel _activeTab = null!;
    private bool _isDetailsPaneOpen = true;
    private bool _isSidebarOpen = true;
    private WorkspacePageKind _workspacePage = WorkspacePageKind.Files;
    private ExtensionDiscoveryService? _extensionDiscovery;
    private CodeEditorViewModel? _codeEditor;
    private ExtensionsViewModel? _extensions;

    /// <summary>Paths of the most recently closed tabs, newest first (Files: reopen closed tab).</summary>
    private readonly List<string> _closedTabPaths = [];
    private const int MaxClosedTabs = 20;

    private ShellViewModel()
    {
        NewTabCommand = new RelayCommand(_ => AddTab());
        OpenCodeEditorCommand = new RelayCommand(_ => WorkspacePage = WorkspacePageKind.Code);
        OpenExtensionsCommand = new RelayCommand(_ =>
        {
            _ = Extensions;
            WorkspacePage = WorkspacePageKind.Extensions;
        });
        CloseWorkspacePageCommand = new RelayCommand(_ => CloseWorkspacePage());
        ToggleDetailsPaneCommand = new RelayCommand(_ =>
        {
            IsDetailsPaneOpen = !IsDetailsPaneOpen;
            foreach (var tab in Tabs) tab.RaiseDetailsPaneChanged();
        });
        ToggleSidebarCommand = new RelayCommand(_ => IsSidebarOpen = !IsSidebarOpen);
        CycleLanguageCommand = new RelayCommand(_ =>
        {
            Loc.SetLanguage(Loc.NextLanguage());
            OnPropertyChanged(nameof(LanguageLabel));
            OnPropertyChanged(nameof(LanguageTooltip));
            (Application.Current as App)?.RestartMainWindow();
        });

        Commands = new CommandManager(this);
    }

    public ObservableCollection<MainViewModel> Tabs { get; } = [];

    /// <summary>
    /// Files App's CommandManager equivalent: one registry shared by the toolbar,
    /// context menus and keyboard shortcuts. Commands resolve the active tab lazily.
    /// </summary>
    public CommandManager Commands { get; }

    public MainViewModel ActiveTab { get => _activeTab; private set => Set(ref _activeTab, value); }

    public bool IsDetailsPaneOpen { get => _isDetailsPaneOpen; private set => Set(ref _isDetailsPaneOpen, value); }

    /// <summary>Sidebar visibility, toggled by the NavigationToolbar hamburger (Files App parity).</summary>
    public bool IsSidebarOpen { get => _isSidebarOpen; private set => Set(ref _isSidebarOpen, value); }

    /// <summary>Two-way binding target for FATabView.SelectedItem; Activate() owns the real state.</summary>
    public MainViewModel? SelectedTab
    {
        get => ActiveTab;
        set
        {
            if (value is not null && !ReferenceEquals(value, ActiveTab)) Activate(value);
        }
    }

    public string LanguageLabel => Loc.CurrentLanguageLabel;

    /// <summary>Shows whether the app is following the OS language, and which signal was used.</summary>
    public string LanguageTooltip => Loc.IsAuto
        ? string.Format(Loc.T("LangAutoHint"), $"{Loc.DetectedLanguage} / {Loc.DetectionSource}")
        : Loc.T("LangManualHint");

    public ICommand NewTabCommand { get; }
    public ICommand ToggleDetailsPaneCommand { get; }
    public ICommand ToggleSidebarCommand { get; }
    public ICommand CycleLanguageCommand { get; }
    public ICommand OpenCodeEditorCommand { get; }
    public ICommand OpenExtensionsCommand { get; }
    public ICommand CloseWorkspacePageCommand { get; }

    /// <summary>Full-area overlay: Files, the Monaco editor, or the VS Code extensions page.</summary>
    public WorkspacePageKind WorkspacePage
    {
        get => _workspacePage;
        private set
        {
            if (!Set(ref _workspacePage, value)) return;
            OnPropertyChanged(nameof(IsWorkspacePageOpen));
            OnPropertyChanged(nameof(IsCodePageOpen));
            OnPropertyChanged(nameof(IsExtensionsPageOpen));
        }
    }

    public bool IsWorkspacePageOpen => WorkspacePage != WorkspacePageKind.Files;
    public bool IsCodePageOpen => WorkspacePage == WorkspacePageKind.Code;
    public bool IsExtensionsPageOpen => WorkspacePage == WorkspacePageKind.Extensions;

    public ExtensionDiscoveryService ExtensionDiscovery => _extensionDiscovery ??= new ExtensionDiscoveryService();
    public CodeEditorViewModel CodeEditor => _codeEditor ??= new CodeEditorViewModel(ExtensionDiscovery) { Shell = this };
    public ExtensionsViewModel Extensions => _extensions ??= new ExtensionsViewModel(ExtensionDiscovery, CodeEditor) { Shell = this };

    /// <summary>Opens a source file in the Monaco editor page.</summary>
    public async Task OpenCodeFileAsync(string path)
    {
        await CodeEditor.OpenAsync(path);
        WorkspacePage = WorkspacePageKind.Code;
    }

    public void CloseWorkspacePage()
    {
        WorkspacePage = WorkspacePageKind.Files;
    }

    /// <summary>Forwarded so the tab-strip footer can switch theme without reaching into a tab.</summary>
    public ICommand ToggleThemeCommand => ActiveTab.ToggleThemeCommand;

    public MainViewModel EnsureFirstTab()
    {
        if (Tabs.Count == 0)
        {
            var paths = LoadSession();
            if (paths.Count > 0)
            {
                foreach (var path in paths) AddTab(path);
            }
            else AddTab();
        }
        return ActiveTab;
    }

    /// <summary>Creates a tab. When <paramref name="path"/> is supplied the tab opens it on init.</summary>
    public void AddTab(string? path = null)
    {
        var tab = new MainViewModel(CreateUiServices()) { Shell = this, InitialPath = path ?? string.Empty };
        tab.RaiseDetailsPaneChanged();
        Tabs.Add(tab);
        Activate(tab);
    }

    public void Activate(MainViewModel tab)
    {
        if (!Tabs.Contains(tab)) return;
        foreach (var item in Tabs) item.IsActive = ReferenceEquals(item, tab);
        if (Set(ref _activeTab, tab)) OnPropertyChanged(nameof(SelectedTab));
    }

    /// <summary>Activates a tab by its 1-based strip position (Files: Ctrl+1..8).</summary>
    public void ActivateByIndex(int index)
    {
        if (index >= 1 && index <= Tabs.Count) Activate(Tabs[index - 1]);
    }

    public void ActivateNextTab()
    {
        if (Tabs.Count < 2) return;
        var index = Tabs.IndexOf(ActiveTab);
        Activate(Tabs[(index + 1) % Tabs.Count]);
    }

    public void ActivatePreviousTab()
    {
        if (Tabs.Count < 2) return;
        var index = Tabs.IndexOf(ActiveTab);
        Activate(Tabs[(index - 1 + Tabs.Count) % Tabs.Count]);
    }

    /// <summary>Moves a tab within the strip; the ObservableCollection event updates the view.</summary>
    public void MoveTab(MainViewModel tab, int newIndex)
    {
        var oldIndex = Tabs.IndexOf(tab);
        if (oldIndex < 0 || newIndex < 0 || newIndex >= Tabs.Count || oldIndex == newIndex) return;
        Tabs.Move(oldIndex, newIndex);
    }

    /// <summary>Moves the active tab left/right by one slot.</summary>
    public void MoveActiveTab(int delta) => MoveTab(ActiveTab, Tabs.IndexOf(ActiveTab) + delta);

    public void CloseTab(MainViewModel tab)
    {
        if (Tabs.Count <= 1 || !Tabs.Contains(tab)) return;
        var index = Tabs.IndexOf(tab);
        RememberClosed(tab);
        Tabs.Remove(tab);
        Activate(Tabs[Math.Min(index, Tabs.Count - 1)]);
    }

    public void CloseActiveTab() => CloseTab(ActiveTab);

    public void CloseOtherTabs(MainViewModel keep)
    {
        if (!Tabs.Contains(keep)) return;
        foreach (var tab in Tabs.Where(x => !ReferenceEquals(x, keep)).ToArray())
        {
            RememberClosed(tab);
            Tabs.Remove(tab);
        }
        Activate(keep);
    }

    public void CloseTabsToRight(MainViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0) return;
        foreach (var other in Tabs.Skip(index + 1).ToArray())
        {
            RememberClosed(other);
            Tabs.Remove(other);
        }
        Activate(tab);
    }

    /// <summary>Opens the last closed folder in a fresh tab (Files: Ctrl+Shift+T).</summary>
    public void ReopenClosedTab()
    {
        if (_closedTabPaths.Count == 0) return;
        var path = _closedTabPaths[0];
        _closedTabPaths.RemoveAt(0);
        AddTab(path);
    }

    public void DuplicateActiveTab() => AddTab(ActiveTab.CurrentPath);

    private void RememberClosed(MainViewModel tab)
    {
        var path = tab.CurrentPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        _closedTabPaths.Remove(path);
        _closedTabPaths.Insert(0, path);
        if (_closedTabPaths.Count > MaxClosedTabs) _closedTabPaths.RemoveAt(_closedTabPaths.Count - 1);
    }

    public async Task InitializeAsync()
    {
        foreach (var tab in Tabs.Where(x => !x.IsReady).ToArray())
            await tab.InitializeAsync();
    }

    /// <summary>Persists open folders so the next launch restores the same tabs.</summary>
    public void SaveSession()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileHub");
            Directory.CreateDirectory(dir);
            var paths = Tabs.Where(x => !string.IsNullOrWhiteSpace(x.CurrentPath))
                .Select(x => x.CurrentPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            File.WriteAllText(Path.Combine(dir, "tabs.json"), JsonSerializer.Serialize(paths));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only profile must not prevent the app from closing.
        }
    }

    private static List<string> LoadSession()
    {
        try
        {
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FileHub", "tabs.json");
            if (!File.Exists(file)) return [];
            var paths = JsonSerializer.Deserialize<string[]>(File.ReadAllText(file)) ?? [];
            return paths.Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// UiServices resolves the current window lazily instead of capturing one at construction
    /// time, because RestartMainWindow() replaces the window on a language switch.
    /// </summary>
    private static UiServices CreateUiServices() => new()
    {
        OpenFileAsync = path => Task.Run(() =>
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })),
        PromptAsync = (title, message, initial, primary) => WithWindow(
            w => new DialogService(w).PromptAsync(title, message, initial, primary),
            Task.FromResult<string?>(null)),
        ConfirmAsync = (title, message) => WithWindow(
            w => new DialogService(w).ConfirmAsync(title, message),
            Task.FromResult(false)),
        ShowInfoAsync = (title, message) => WithWindow(
            w => new DialogService(w).ShowInfoAsync(title, message),
            Task.CompletedTask),
        CopyTextAsync = text => WithWindow(
            w => new DialogService(w).CopyTextAsync(text),
            Task.CompletedTask),
        Reveal = PlatformService.Reveal,
        SetTheme = mode => (Application.Current as App)?.SetTheme(mode),
        SelectAll = () => CurrentWindow()?.SelectAllEntries()
    };

    private static T WithWindow<T>(Func<Window, T> action, T fallback)
        => CurrentWindow() is { } window ? action(window) : fallback;

    private static MainWindow? CurrentWindow()
        => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?
            .MainWindow as MainWindow;
}
