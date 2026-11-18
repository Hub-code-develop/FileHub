using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using FileHub.Desktop.Actions;
using FileHub.Desktop.Services;
using FileHub.Desktop.Views;
using FileHub.Files.Services;

namespace FileHub.Desktop.ViewModels;

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

    private ShellViewModel()
    {
        NewTabCommand = new RelayCommand(_ => AddTab());
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

    /// <summary>Forwarded so the tab-strip footer can switch theme without reaching into a tab.</summary>
    public ICommand ToggleThemeCommand => ActiveTab.ToggleThemeCommand;

    public MainViewModel EnsureFirstTab()
    {
        if (Tabs.Count == 0) AddTab();
        return ActiveTab;
    }

    public void AddTab()
    {
        var tab = new MainViewModel(CreateUiServices()) { Shell = this };
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

    public void CloseTab(MainViewModel tab)
    {
        if (Tabs.Count <= 1 || !Tabs.Contains(tab)) return;
        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        Activate(Tabs[Math.Min(index, Tabs.Count - 1)]);
    }

    public async Task InitializeAsync()
    {
        foreach (var tab in Tabs.Where(x => !x.IsReady).ToArray())
            await tab.InitializeAsync();
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
