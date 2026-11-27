using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using FileHub.Desktop.Services;

namespace FileHub.Desktop.ViewModels;

/// <summary>
/// State behind the settings dialog. Every value is a thin proxy over <see cref="Preferences"/>
/// (and, for window chrome, over the shell): the dialog owns nothing, so closing it with Esc or
/// the X leaves every change applied. Sections are grouped the way VS Code groups its settings —
/// a rail on the left, related rows inside each page, immediate effect, no Apply button.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly ShellViewModel _shell;

    private static Preferences P => Preferences.Current;

    public SettingsViewModel(ShellViewModel shell)
    {
        _shell = shell;

        ThemeOptions = [Loc.T("ThemeSystem"), Loc.T("ThemeLight"), Loc.T("ThemeDark")];
        CodeWrapOptions = [Loc.T("SettingsWrapOff"), Loc.T("SettingsWrapOn"), Loc.T("SettingsWrapColumn")];
        CodeLineNumberOptions = [Loc.T("SettingsLineNumbersOn"), Loc.T("SettingsLineNumbersRelative"), Loc.T("SettingsLineNumbersOff")];
        CodeWhitespaceOptions = [Loc.T("SettingsWhitespaceNone"), Loc.T("SettingsWhitespaceBoundary"), Loc.T("SettingsWhitespaceSelection"), Loc.T("SettingsWhitespaceAll")];
        CodeCursorOptions = [Loc.T("SettingsCursorLine"), Loc.T("SettingsCursorBlock"), Loc.T("SettingsCursorUnderline")];
        CodeAutoSaveOptions = [Loc.T("SettingsAutoSaveOff"), Loc.T("SettingsAutoSaveDelay")];
        FileSortOptions = [Loc.T("SortName"), Loc.T("SortModified"), Loc.T("SortSize"), Loc.T("SortKind")];
        OfficeKindOptions = ["Word", "Excel", "PowerPoint"];
        MacroSecurityOptions = [Loc.T("SettingsMacroDisabled"), Loc.T("SettingsMacroPrompt"), Loc.T("SettingsMacroEnabled")];

        // The theme list only exists once the extension discovery ran; do it here so the editor
        // theme combo is never empty when settings is the first thing opened.
        shell.CodeEditor.RefreshThemeList();

        OpenNoticesCommand = new RelayCommand(_ => OpenLocalFile("THIRD-PARTY-NOTICES.md"));
        OpenLicenseCommand = new RelayCommand(_ => OpenLocalFile("LICENSE"));
        OpenProjectCommand = new RelayCommand(_ => OpenUrl("https://github.com/Hub-code-develop/FileHub"));
        ResetCommand = new RelayCommand(_ => ResetToDefaults());
    }

    private MainViewModel Tab => _shell.ActiveTab;

    public ExtensionsViewModel Extensions => _shell.Extensions;

    public IReadOnlyList<string> ThemeOptions { get; }
    public IReadOnlyList<string> CodeWrapOptions { get; }
    public IReadOnlyList<string> CodeLineNumberOptions { get; }
    public IReadOnlyList<string> CodeWhitespaceOptions { get; }
    public IReadOnlyList<string> CodeCursorOptions { get; }
    public IReadOnlyList<string> CodeAutoSaveOptions { get; }
    public IReadOnlyList<string> FileSortOptions { get; }
    public IReadOnlyList<string> OfficeKindOptions { get; }
    public IReadOnlyList<string> MacroSecurityOptions { get; }

    public ICommand OpenNoticesCommand { get; }
    public ICommand OpenLicenseCommand { get; }
    public ICommand OpenProjectCommand { get; }
    public ICommand ResetCommand { get; }

    // ---------------------------------------------------------------- general

    /// <summary>0 = system, 1 = light, 2 = dark.</summary>
    public int ThemeIndex
    {
        get => P.ThemeMode switch { "light" => 1, "dark" => 2, _ => 0 };
        set
        {
            Tab.ThemeMode = value switch { 1 => "light", 2 => "dark", _ => "system" };
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

    /// <summary>Rebuilding the window is destructive for a modal child, so the dialog closes first.</summary>
    public void CycleLanguage() => _shell.CycleLanguageCommand.Execute(null);

    // ---------------------------------------------------------------- file manager

    public bool ShowHiddenFiles
    {
        get => P.ShowHiddenFiles;
        set
        {
            if (value == P.ShowHiddenFiles) return;
            P.ShowHiddenFiles = value;
            P.Save();
            _shell.ApplyFilesPreferences();
            OnPropertyChanged();
        }
    }

    public bool ShowFileExtensions
    {
        get => P.ShowFileExtensions;
        set
        {
            if (value == P.ShowFileExtensions) return;
            P.ShowFileExtensions = value;
            P.Save();
            _shell.ApplyFilesPreferences();
            OnPropertyChanged();
        }
    }

    public bool FoldersFirst
    {
        get => P.FoldersFirst;
        set
        {
            if (value == P.FoldersFirst) return;
            P.FoldersFirst = value;
            P.Save();
            _shell.ApplyFilesPreferences();
            OnPropertyChanged();
        }
    }

    public bool DefaultGridView
    {
        get => P.DefaultGridView;
        set
        {
            if (value == P.DefaultGridView) return;
            P.DefaultGridView = value;
            P.Save();
            _shell.ApplyFilesPreferences();
            OnPropertyChanged();
        }
    }

    public bool ShowThumbnails
    {
        get => P.ShowThumbnails;
        set
        {
            if (value == P.ShowThumbnails) return;
            P.ShowThumbnails = value;
            P.Save();
            _shell.ApplyFilesPreferences();
            OnPropertyChanged();
        }
    }

    public bool ConfirmDelete
    {
        get => P.ConfirmDelete;
        set
        {
            if (value == P.ConfirmDelete) return;
            P.ConfirmDelete = value;
            P.Save();
            OnPropertyChanged();
        }
    }

    public int SortIndex
    {
        get => P.SortColumn switch { "Modified" => 1, "Size" => 2, "Kind" => 3, _ => 0 };
        set
        {
            var column = value switch { 1 => "Modified", 2 => "Size", 3 => "Kind", _ => "Name" };
            if (column == P.SortColumn) return;
            P.SortColumn = column;
            P.Save();
            _shell.ApplyFilesPreferences();
            OnPropertyChanged();
        }
    }

    public bool SortDescending
    {
        get => P.SortDescending;
        set
        {
            if (value == P.SortDescending) return;
            P.SortDescending = value;
            P.Save();
            _shell.ApplyFilesPreferences();
            OnPropertyChanged();
        }
    }

    // ---------------------------------------------------------------- code editor

    public string CodeFontFamily
    {
        get => P.CodeFontFamily;
        set
        {
            var next = string.IsNullOrWhiteSpace(value) ? "Menlo, Consolas, monospace" : value.Trim();
            if (next == P.CodeFontFamily) return;
            P.CodeFontFamily = next;
            P.Save();
            _shell.ApplyCodePreferences();
            OnPropertyChanged();
        }
    }

    public decimal? CodeFontSize
    {
        get => (decimal)P.CodeFontSize;
        set
        {
            if (value is null) return;
            var next = (int)Math.Clamp(value.Value, 8, 48);
            if (next == P.CodeFontSize) return;
            P.CodeFontSize = next;
            P.Save();
            _shell.ApplyCodePreferences();
            OnPropertyChanged();
        }
    }

    public decimal? CodeTabSize
    {
        get => (decimal)P.CodeTabSize;
        set
        {
            if (value is null) return;
            var next = (int)Math.Clamp(value.Value, 1, 8);
            if (next == P.CodeTabSize) return;
            P.CodeTabSize = next;
            P.Save();
            _shell.ApplyCodePreferences();
            OnPropertyChanged();
        }
    }

    public bool CodeInsertSpaces
    {
        get => P.CodeInsertSpaces;
        set
        {
            if (value == P.CodeInsertSpaces) return;
            P.CodeInsertSpaces = value;
            P.Save();
            _shell.ApplyCodePreferences();
            OnPropertyChanged();
        }
    }

    public int CodeWrapIndex
    {
        get => P.CodeWordWrap switch { "on" => 1, "wordWrapColumn" => 2, _ => 0 };
        set
        {
            var next = value switch { 1 => "on", 2 => "wordWrapColumn", _ => "off" };
            if (next == P.CodeWordWrap) return;
            P.CodeWordWrap = next;
            P.Save();
            _shell.ApplyCodePreferences();
            OnPropertyChanged();
        }
    }

    public bool CodeMinimap
    {
        get => P.CodeMinimap;
        set
        {
            if (value == P.CodeMinimap) return;
            P.CodeMinimap = value;
            P.Save();
            _shell.ApplyCodePreferences();
            OnPropertyChanged();
        }
    }

    public int CodeLineNumbersIndex
    {
        get => P.CodeLineNumbers switch { "relative" => 1, "off" => 2, _ => 0 };
        set
        {
            var next = value switch { 1 => "relative", 2 => "off", _ => "on" };
            if (next == P.CodeLineNumbers) return;
            P.CodeLineNumbers = next;
            P.Save();
            _shell.ApplyCodePreferences();
            OnPropertyChanged();
        }
    }

    public int CodeWhitespaceIndex
    {
        get => P.CodeRenderWhitespace switch { "none" => 0, "boundary" => 1, "all" => 3, _ => 2 };
        set
        {
            var next = value switch { 0 => "none", 1 => "boundary", 3 => "all", _ => "selection" };
            if (next == P.CodeRenderWhitespace) return;
            P.CodeRenderWhitespace = next;
            P.Save();
            _shell.ApplyCodePreferences();
            OnPropertyChanged();
        }
    }

    public bool CodeBracketPairs
    {
        get => P.CodeBracketPairs;
        set
        {
            if (value == P.CodeBracketPairs) return;
            P.CodeBracketPairs = value;
            P.Save();
            _shell.ApplyCodePreferences();
            OnPropertyChanged();
        }
    }

    public int CodeCursorIndex
    {
        get => P.CodeCursorStyle switch { "block" => 1, "underline" => 2, _ => 0 };
        set
        {
            var next = value switch { 1 => "block", 2 => "underline", _ => "line" };
            if (next == P.CodeCursorStyle) return;
            P.CodeCursorStyle = next;
            P.Save();
            _shell.ApplyCodePreferences();
            OnPropertyChanged();
        }
    }

    public bool CodeSmoothScrolling
    {
        get => P.CodeSmoothScrolling;
        set
        {
            if (value == P.CodeSmoothScrolling) return;
            P.CodeSmoothScrolling = value;
            P.Save();
            _shell.ApplyCodePreferences();
            OnPropertyChanged();
        }
    }

    public int CodeAutoSaveIndex
    {
        get => P.CodeAutoSave == "afterDelay" ? 1 : 0;
        set
        {
            var next = value == 1 ? "afterDelay" : "off";
            if (next == P.CodeAutoSave) return;
            P.CodeAutoSave = next;
            P.Save();
            _shell.ApplyCodePreferences();
            OnPropertyChanged();
        }
    }

    public decimal? CodeAutoSaveDelay
    {
        get => (decimal)P.CodeAutoSaveDelayMs;
        set
        {
            if (value is null) return;
            var next = (int)Math.Clamp(value.Value, 200, 10000);
            if (next == P.CodeAutoSaveDelayMs) return;
            P.CodeAutoSaveDelayMs = next;
            P.Save();
            _shell.ApplyCodePreferences();
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<string> CodeThemeOptions => _shell.CodeEditor.ThemeOptions;

    public string CodeTheme
    {
        get => _shell.CodeEditor.SelectedTheme;
        set
        {
            if (value == _shell.CodeEditor.SelectedTheme) return;
            _shell.CodeEditor.SelectedTheme = value;
            OnPropertyChanged();
        }
    }

    // ---------------------------------------------------------------- office

    public int OfficeKindIndex
    {
        get => P.OfficeDefaultKind switch { "Excel" => 1, "PowerPoint" => 2, _ => 0 };
        set
        {
            var next = value switch { 1 => "Excel", 2 => "PowerPoint", _ => "Word" };
            if (next == P.OfficeDefaultKind) return;
            P.OfficeDefaultKind = next;
            P.Save();
            _shell.ApplyOfficePreferences();
            OnPropertyChanged();
        }
    }

    public decimal? OfficeFontSize
    {
        get => (decimal)P.OfficeFontSize;
        set
        {
            if (value is null) return;
            var next = (int)Math.Clamp(value.Value, 8, 72);
            if (Math.Abs(P.OfficeFontSize - next) < 0.01) return;
            P.OfficeFontSize = next;
            P.Save();
            _shell.ApplyOfficePreferences();
            OnPropertyChanged();
        }
    }

    public bool OfficeShowRuler
    {
        get => P.OfficeShowRuler;
        set
        {
            if (value == P.OfficeShowRuler) return;
            P.OfficeShowRuler = value;
            P.Save();
            _shell.ApplyOfficePreferences();
            OnPropertyChanged();
        }
    }

    public bool OfficeWidePage
    {
        get => P.OfficeWidePage;
        set
        {
            if (value == P.OfficeWidePage) return;
            P.OfficeWidePage = value;
            P.Save();
            _shell.ApplyOfficePreferences();
            OnPropertyChanged();
        }
    }

    public bool OfficeGridLines
    {
        get => P.OfficeGridLines;
        set
        {
            if (value == P.OfficeGridLines) return;
            P.OfficeGridLines = value;
            P.Save();
            _shell.ApplyOfficePreferences();
            OnPropertyChanged();
        }
    }

    /// <summary>Default for documents created from now on; open documents keep their own flag.</summary>
    public bool OfficeTrackChanges
    {
        get => P.OfficeTrackChanges;
        set
        {
            if (value == P.OfficeTrackChanges) return;
            P.OfficeTrackChanges = value;
            P.Save();
            OnPropertyChanged();
        }
    }

    public bool OfficeAutoSave
    {
        get => P.OfficeAutoSave;
        set
        {
            if (value == P.OfficeAutoSave) return;
            P.OfficeAutoSave = value;
            P.Save();
            _shell.ApplyOfficePreferences();
            OnPropertyChanged();
        }
    }

    public decimal? OfficeAutoSaveSeconds
    {
        get => (decimal)P.OfficeAutoSaveSeconds;
        set
        {
            if (value is null) return;
            var next = (int)Math.Clamp(value.Value, 10, 3600);
            if (next == P.OfficeAutoSaveSeconds) return;
            P.OfficeAutoSaveSeconds = next;
            P.Save();
            _shell.ApplyOfficePreferences();
            OnPropertyChanged();
        }
    }

    public int MacroSecurityIndex
    {
        get => P.OfficeMacroSecurity switch { "Disabled" => 0, "Enabled" => 2, _ => 1 };
        set
        {
            var next = value switch { 0 => "Disabled", 2 => "Enabled", _ => "Prompt" };
            if (next == P.OfficeMacroSecurity) return;
            P.OfficeMacroSecurity = next;
            P.Save();
            _shell.ApplyOfficePreferences();
            OnPropertyChanged();
        }
    }

    public decimal? OfficeZoom
    {
        get => (decimal)P.OfficeZoom;
        set
        {
            if (value is null) return;
            var next = (int)Math.Clamp(value.Value, 50, 200);
            if (next == P.OfficeZoom) return;
            P.OfficeZoom = next;
            P.Save();
            _shell.ApplyOfficePreferences();
            OnPropertyChanged();
        }
    }

    // ---------------------------------------------------------------- about

    public string BuildLabel => BuildInfo.DisplayLabel;
    public string BuildDetail => BuildInfo.DetailText;
    public string VersionText => string.IsNullOrWhiteSpace(BuildInfo.Version) ? "-" : BuildInfo.Version;

    // ---------------------------------------------------------------- actions

    /// <summary>Restores every documented default and pushes them to the live surfaces.</summary>
    private void ResetToDefaults()
    {
        P.ResetToDefaults();
        P.Save();

        // Window chrome is not part of Preferences; put the open window back to its default too.
        if (!_shell.IsSidebarOpen) _shell.ToggleSidebarCommand.Execute(null);
        if (!_shell.IsDetailsPaneOpen) _shell.ToggleDetailsPaneCommand.Execute(null);

        // Theme lives on the application, not in the shell: apply the restored value explicitly.
        _shell.ActiveTab.ThemeMode = P.ThemeMode;
        _shell.ApplyPreferences();
        OnPropertyChanged(string.Empty);
    }

    private void OpenLocalFile(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        if (!File.Exists(path)) return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static void OpenUrl(string url)
        => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
