using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using FileHub.Desktop.Services;

namespace FileHub.Desktop.ViewModels;

/// <summary>
/// State behind the settings dialog. Every value is a thin proxy over the shell or the active tab:
/// the dialog never owns preferences of its own, it edits the live ones, so closing it with the
/// X leaves every change applied (this is a settings panel, not a form with an OK button).
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly ShellViewModel _shell;

    public SettingsViewModel(ShellViewModel shell)
    {
        _shell = shell;
        // Index order must match the ThemeModes array; the combo box binds to the index only.
        ThemeOptions = [Loc.T("ThemeSystem"), Loc.T("ThemeLight"), Loc.T("ThemeDark")];
        OpenNoticesCommand = new RelayCommand(_ => OpenLocalFile("THIRD-PARTY-NOTICES.md"));
        OpenLicenseCommand = new RelayCommand(_ => OpenLocalFile("LICENSE"));
        OpenProjectCommand = new RelayCommand(_ => OpenUrl("https://github.com/Hub-code-develop/FileHub"));
    }

    private MainViewModel Tab => _shell.ActiveTab;

    public ExtensionsViewModel Extensions => _shell.Extensions;

    public IReadOnlyList<string> ThemeOptions { get; }

    public ICommand OpenNoticesCommand { get; }
    public ICommand OpenLicenseCommand { get; }
    public ICommand OpenProjectCommand { get; }

    /// <summary>0 = system, 1 = light, 2 = dark.</summary>
    public int ThemeIndex
    {
        get => Tab.ThemeMode switch { "light" => 1, "dark" => 2, _ => 0 };
        set
        {
            Tab.ThemeMode = value switch { 1 => "light", 2 => "dark", _ => "system" };
            OnPropertyChanged();
        }
    }

    public bool ShowHiddenFiles
    {
        get => Tab.ShowHiddenFiles;
        set
        {
            if (value == Tab.ShowHiddenFiles) return;
            Tab.ToggleHiddenCommand.Execute(null);
            OnPropertyChanged();
        }
    }

    public bool ShowDetailsPane
    {
        get => _shell.IsDetailsPaneOpen;
        set
        {
            if (value == _shell.IsDetailsPaneOpen) return;
            _shell.ToggleDetailsPaneCommand.Execute(null);
            OnPropertyChanged();
        }
    }

    public bool ShowSidebar
    {
        get => _shell.IsSidebarOpen;
        set
        {
            if (value == _shell.IsSidebarOpen) return;
            _shell.ToggleSidebarCommand.Execute(null);
            OnPropertyChanged();
        }
    }

    public bool DualPane
    {
        get => _shell.IsDualPaneOpen;
        set
        {
            if (value == _shell.IsDualPaneOpen) return;
            _shell.ToggleDualPaneCommand.Execute(null);
            OnPropertyChanged();
        }
    }

    public string LanguageLabel => _shell.LanguageLabel;
    public string LanguageHint => _shell.LanguageTooltip;

    public string BuildLabel => BuildInfo.DisplayLabel;
    public string BuildDetail => BuildInfo.DetailText;
    public string VersionText => string.IsNullOrWhiteSpace(BuildInfo.Version) ? "-" : BuildInfo.Version;

    /// <summary>Rebuilding the window is destructive for a modal child, so the dialog closes first.</summary>
    public void CycleLanguage() => _shell.CycleLanguageCommand.Execute(null);

    private void OpenLocalFile(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        if (!File.Exists(path)) return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static void OpenUrl(string url)
        => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
