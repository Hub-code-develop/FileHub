using System.Text.Json;

namespace FileHub.Desktop.Services;

/// <summary>
/// Persisted user preferences. One JSON document under the per-user application data folder;
/// the settings dialog edits this object directly and every writer calls <see cref="Save"/> so a
/// crash or a relaunch never loses a toggle. Values are intentionally plain (string/bool/int) so
/// the file stays readable and forward compatible when a key is added or dropped.
/// </summary>
public sealed class Preferences
{
    private static Preferences? _current;
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>The live preference set. Loading is lazy and never throws.</summary>
    public static Preferences Current => _current ??= Load();

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FileHub", "preferences.json");

    // ---------------------------------------------------------------- appearance

    /// <summary>"system", "light" or "dark".</summary>
    public string ThemeMode { get; set; } = "system";

    // ---------------------------------------------------------------- file manager

    public bool ShowHiddenFiles { get; set; }
    public bool ShowFileExtensions { get; set; } = true;
    public bool ShowSidebar { get; set; } = true;
    public bool ShowDetailsPane { get; set; } = true;
    public bool FoldersFirst { get; set; } = true;
    public bool DefaultGridView { get; set; }
    public string SortColumn { get; set; } = "Name";
    public bool SortDescending { get; set; }
    public bool ConfirmDelete { get; set; } = true;
    public bool ShowThumbnails { get; set; } = true;

    // ---------------------------------------------------------------- code editor

    public string CodeFontFamily { get; set; } = "Menlo, Consolas, monospace";
    public int CodeFontSize { get; set; } = 13;
    public int CodeTabSize { get; set; } = 4;
    public bool CodeInsertSpaces { get; set; } = true;
    /// <summary>Monaco wordWrap: "off", "on" or "wordWrapColumn".</summary>
    public string CodeWordWrap { get; set; } = "off";
    public bool CodeMinimap { get; set; } = true;
    /// <summary>Monaco lineNumbers: "on", "off" or "relative".</summary>
    public string CodeLineNumbers { get; set; } = "on";
    /// <summary>Monaco renderWhitespace: "none", "boundary", "selection" or "all".</summary>
    public string CodeRenderWhitespace { get; set; } = "selection";
    public bool CodeBracketPairs { get; set; } = true;
    /// <summary>Monaco cursorStyle: "line", "block" or "underline".</summary>
    public string CodeCursorStyle { get; set; } = "line";
    public bool CodeSmoothScrolling { get; set; }
    /// <summary>"off" or "afterDelay".</summary>
    public string CodeAutoSave { get; set; } = "off";
    public int CodeAutoSaveDelayMs { get; set; } = 1200;

    // ---------------------------------------------------------------- office

    /// <summary>"Word", "Excel" or "PowerPoint" — the kind preselected for a new document.</summary>
    public string OfficeDefaultKind { get; set; } = "Word";
    /// <summary>Body font size in points for new Word documents.</summary>
    public double OfficeFontSize { get; set; } = 14;
    public bool OfficeShowRuler { get; set; } = true;
    /// <summary>True renders the sheet full width instead of an 860px paper column.</summary>
    public bool OfficeWidePage { get; set; }
    public bool OfficeTrackChanges { get; set; }
    public bool OfficeAutoSave { get; set; } = true;
    public int OfficeAutoSaveSeconds { get; set; } = 60;
    public bool OfficeGridLines { get; set; } = true;
    /// <summary>"Disabled", "Prompt" or "Enabled" — mirrors MacroSecurityLevel.</summary>
    public string OfficeMacroSecurity { get; set; } = "Prompt";
    public int OfficeZoom { get; set; } = 100;

    public void Save()
    {
        try
        {
            var folder = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Preferences are a convenience: a read-only profile must not break the app.
        }
    }

    /// <summary>Clamps out-of-range values that a hand-edited file could contain.</summary>
    public void Normalize()
    {
        ThemeMode = ThemeMode is "light" or "dark" ? ThemeMode : "system";
        if (CodeFontSize is < 8 or > 48) CodeFontSize = 13;
        if (CodeTabSize is < 1 or > 8) CodeTabSize = 4;
        if (CodeAutoSaveDelayMs is < 200 or > 10000) CodeAutoSaveDelayMs = 1200;
        if (OfficeFontSize is < 8 or > 72) OfficeFontSize = 14;
        if (OfficeAutoSaveSeconds is < 10 or > 3600) OfficeAutoSaveSeconds = 60;
        if (OfficeZoom is < 50 or > 200) OfficeZoom = 100;
        OfficeDefaultKind = OfficeDefaultKind is "Excel" or "PowerPoint" ? OfficeDefaultKind : "Word";
        CodeWordWrap = CodeWordWrap is "on" or "wordWrapColumn" ? CodeWordWrap : "off";
        CodeLineNumbers = CodeLineNumbers is "off" or "relative" ? CodeLineNumbers : "on";
        CodeRenderWhitespace = CodeRenderWhitespace is "none" or "boundary" or "all"
            ? CodeRenderWhitespace : "selection";
        CodeCursorStyle = CodeCursorStyle is "block" or "underline" ? CodeCursorStyle : "line";
        CodeAutoSave = CodeAutoSave == "afterDelay" ? CodeAutoSave : "off";
        OfficeMacroSecurity = OfficeMacroSecurity is "Disabled" or "Enabled" ? OfficeMacroSecurity : "Prompt";
    }

    /// <summary>Restores every documented default in place (the file is rewritten by the caller).</summary>
    public void ResetToDefaults()
    {
        var d = new Preferences();
        ThemeMode = d.ThemeMode;
        ShowHiddenFiles = d.ShowHiddenFiles;
        ShowFileExtensions = d.ShowFileExtensions;
        ShowSidebar = d.ShowSidebar;
        ShowDetailsPane = d.ShowDetailsPane;
        FoldersFirst = d.FoldersFirst;
        DefaultGridView = d.DefaultGridView;
        SortColumn = d.SortColumn;
        SortDescending = d.SortDescending;
        ConfirmDelete = d.ConfirmDelete;
        ShowThumbnails = d.ShowThumbnails;

        CodeFontFamily = d.CodeFontFamily;
        CodeFontSize = d.CodeFontSize;
        CodeTabSize = d.CodeTabSize;
        CodeInsertSpaces = d.CodeInsertSpaces;
        CodeWordWrap = d.CodeWordWrap;
        CodeMinimap = d.CodeMinimap;
        CodeLineNumbers = d.CodeLineNumbers;
        CodeRenderWhitespace = d.CodeRenderWhitespace;
        CodeBracketPairs = d.CodeBracketPairs;
        CodeCursorStyle = d.CodeCursorStyle;
        CodeSmoothScrolling = d.CodeSmoothScrolling;
        CodeAutoSave = d.CodeAutoSave;
        CodeAutoSaveDelayMs = d.CodeAutoSaveDelayMs;

        OfficeDefaultKind = d.OfficeDefaultKind;
        OfficeFontSize = d.OfficeFontSize;
        OfficeShowRuler = d.OfficeShowRuler;
        OfficeWidePage = d.OfficeWidePage;
        OfficeTrackChanges = d.OfficeTrackChanges;
        OfficeAutoSave = d.OfficeAutoSave;
        OfficeAutoSaveSeconds = d.OfficeAutoSaveSeconds;
        OfficeGridLines = d.OfficeGridLines;
        OfficeMacroSecurity = d.OfficeMacroSecurity;
        OfficeZoom = d.OfficeZoom;
    }

    private static Preferences Load()
    {
        var prefs = new Preferences();
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath), Options);
                if (loaded is not null) prefs = loaded;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt file falls back to defaults; the next Save rewrites it.
        }
        prefs.Normalize();
        return prefs;
    }
}
