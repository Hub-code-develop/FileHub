using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Input;
using Avalonia.Threading;
using FileHub.Code.Services;
using FileHub.Desktop.Services;

namespace FileHub.Desktop.ViewModels;

/// <summary>
/// State for the Monaco-backed code editor page. The view owns the native WebView; this class owns
/// file I/O, language detection and the VS Code contribution data sent into the editor.
/// </summary>
public sealed class CodeEditorViewModel : ViewModelBase
{
    private readonly ExtensionDiscoveryService _extensions;
    private string _filePath = string.Empty;
    private string _language = "plaintext";
    private string _encodingName = "UTF-8";
    private string _cursor = "1, 1";
    private string _status = string.Empty;
    private bool _isDirty;
    private bool _engineReady;
    private string _selectedTheme = string.Empty;
    private DispatcherTimer? _autoSaveTimer;

    public CodeEditorViewModel(ExtensionDiscoveryService extensions)
    {
        _extensions = extensions;
        SaveCommand = new RelayCommand(_ => SaveRequested?.Invoke(), _ => HasFile && IsDirty);
        CloseCommand = new RelayCommand(_ => Shell?.CloseWorkspacePage());
        RefreshContributionsCommand = new RelayCommand(_ => RefreshThemes());
    }

    public ShellViewModel? Shell { get; set; }

    public ICommand SaveCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand RefreshContributionsCommand { get; }

    /// <summary>Fired when the WebView engine is ready for a new document.</summary>
    public event Action<string>? FilePayloadReady;
    public event Action<string>? ThemePayloadReady;
    public event Action<string>? SnippetsPayloadReady;
    public event Action<string>? OptionsPayloadReady;
    public event Action? SaveRequested;

    public string FilePath { get => _filePath; private set => Set(ref _filePath, value); }
    public string FileName => string.IsNullOrEmpty(FilePath) ? string.Empty : Path.GetFileName(FilePath);
    public bool HasFile => !string.IsNullOrEmpty(FilePath);

    public string Language
    {
        get => _language;
        private set { if (Set(ref _language, value)) OnPropertyChanged(nameof(LanguageLabel)); }
    }

    public string LanguageLabel => string.IsNullOrEmpty(Language) ? Loc.T("CodePlainText") : Language;
    public string EncodingName { get => _encodingName; private set => Set(ref _encodingName, value); }
    public string Cursor { get => _cursor; private set => Set(ref _cursor, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (!Set(ref _isDirty, value)) return;
            RaiseCanSave();
            UpdateAutoSaveTimer();
        }
    }
    public bool EngineReady { get => _engineReady; private set => Set(ref _engineReady, value); }

    public ObservableCollection<string> ThemeOptions { get; } = [];

    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (Set(ref _selectedTheme, value)) ApplyTheme();
        }
    }

    /// <summary>Payload kept for the moment the WebView finishes loading.</summary>
    public string? PendingPayload { get; private set; }
    public string? PendingTheme { get; private set; }
    public string? PendingSnippets { get; private set; }
    public string? PendingOptions { get; private set; }

    /// <summary>
    /// The Monaco option set built from the persisted preferences. Monaco receives these through
    /// <c>fileHub.applyOptions</c>; keeping the mapping in one place means the settings dialog and
    /// the editor toolbar can never drift apart.
    /// </summary>
    public string BuildOptionsJson()
    {
        var p = Preferences.Current;
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["fontFamily"] = p.CodeFontFamily,
            ["fontSize"] = p.CodeFontSize,
            ["tabSize"] = p.CodeTabSize,
            ["insertSpaces"] = p.CodeInsertSpaces,
            ["wordWrap"] = p.CodeWordWrap,
            ["lineNumbers"] = p.CodeLineNumbers,
            ["renderWhitespace"] = p.CodeRenderWhitespace,
            ["cursorStyle"] = p.CodeCursorStyle,
            ["smoothScrolling"] = p.CodeSmoothScrolling,
            ["minimap"] = new Dictionary<string, object?> { ["enabled"] = p.CodeMinimap },
            ["bracketPairColorization"] = new Dictionary<string, object?> { ["enabled"] = p.CodeBracketPairs }
        });
    }

    /// <summary>Re-pushes every editor option; called when the settings dialog changes one.</summary>
    public void ApplyPreferences()
    {
        PendingOptions = BuildOptionsJson();
        // The page replays PendingOptions when it finishes loading; pushing into a WebView that
        // has no engine yet would only produce a bridge error.
        if (EngineReady) OptionsPayloadReady?.Invoke(PendingOptions);
        UpdateAutoSaveTimer();
    }

    /// <summary>Refreshes the theme list without opening a file (used by the settings dialog).</summary>
    public void RefreshThemeList() => RefreshThemes();

    /// <summary>
    /// "Auto save after delay" is implemented on the VM rather than in Monaco: the view owns the
    /// text, so the timer raises SaveRequested and the page round-trips getValue() -> write.
    /// </summary>
    private void UpdateAutoSaveTimer()
    {
        var enabled = Preferences.Current.CodeAutoSave == "afterDelay" && IsDirty && HasFile;
        if (!enabled)
        {
            _autoSaveTimer?.Stop();
            return;
        }

        var delay = TimeSpan.FromMilliseconds(Preferences.Current.CodeAutoSaveDelayMs);
        if (_autoSaveTimer is null)
        {
            _autoSaveTimer = new DispatcherTimer { Interval = delay };
            _autoSaveTimer.Tick += (_, _) =>
            {
                if (IsDirty && HasFile) SaveRequested?.Invoke();
                else _autoSaveTimer?.Stop();
            };
        }
        _autoSaveTimer.Interval = delay;
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    public async Task OpenAsync(string path)
    {
        var loaded = await Task.Run(() => CodeFileService.Read(path));
        FilePath = path;
        EncodingName = loaded.EncodingName;
        Language = CodeLanguageService.GetLanguageId(path, _extensions.LanguageExtensions) ?? "plaintext";
        IsDirty = false;
        Cursor = "1, 1";
        Status = Loc.F("CodeLoaded", loaded.Text.Length);

        var uri = new Uri(Path.GetFullPath(path)).AbsoluteUri;
        PendingPayload = JsonSerializer.Serialize(new
        {
            value = loaded.Text,
            language = Language,
            uri
        });
        FilePayloadReady?.Invoke(PendingPayload);

        RefreshThemes();
        RefreshSnippets();
    }

    public void MarkEngineReady()
    {
        EngineReady = true;
        // Options go first: the model is created by the next payload with the right tab size.
        PendingOptions ??= BuildOptionsJson();
        OptionsPayloadReady?.Invoke(PendingOptions);
        if (PendingPayload is not null) FilePayloadReady?.Invoke(PendingPayload);
        if (PendingTheme is not null) ThemePayloadReady?.Invoke(PendingTheme);
        if (PendingSnippets is not null) SnippetsPayloadReady?.Invoke(PendingSnippets);
    }

    /// <summary>Parses a JS bridge message. Returns true when the message was understood.</summary>
    public bool HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            switch (type)
            {
                case "engine":
                    Status = root.TryGetProperty("message", out var m) ? m.GetString() ?? string.Empty : string.Empty;
                    return true;
                case "dirty":
                    IsDirty = root.TryGetProperty("dirty", out var d) && d.GetBoolean();
                    return true;
                case "cursor":
                    var line = root.TryGetProperty("line", out var l) ? l.GetInt32() : 1;
                    var column = root.TryGetProperty("column", out var c) ? c.GetInt32() : 1;
                    Cursor = $"{line}, {column}";
                    return true;
                case "language":
                    if (root.TryGetProperty("language", out var lang)) Language = lang.GetString() ?? "plaintext";
                    return true;
                case "ready":
                    return true;
                default:
                    return false;
            }
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Saves the text returned by getValue() (a JSON string literal).</summary>
    public async Task<bool> SaveAsync(string jsonValue)
    {
        if (string.IsNullOrEmpty(FilePath)) return false;
        try
        {
            var text = JsonSerializer.Deserialize<string>(jsonValue) ?? string.Empty;
            await Task.Run(() => CodeFileService.Write(FilePath, text, EncodingName));
            IsDirty = false;
            Status = Loc.T("CodeSaved");
            return true;
        }
        catch (Exception ex)
        {
            Status = Loc.F("CodeSaveFailed", ex.Message);
            return false;
        }
    }

    private void RefreshThemes()
    {
        ThemeOptions.Clear();
        ThemeOptions.Add(Loc.T("CodeThemeDefault"));
        foreach (var theme in _extensions.Extensions.Where(x => x.Enabled).SelectMany(x => x.Themes))
            ThemeOptions.Add(theme.Label);
        if (_extensions.Extensions.Where(x => x.Enabled).SelectMany(x => x.Themes).Any())
        {
            ThemeOptions.Add(Loc.T("CodeThemeAuto"));
        }

        var auto = Loc.T("CodeThemeAuto");
        SelectedTheme = ThemeOptions.Contains(auto) ? auto : Loc.T("CodeThemeDefault");
        OnPropertyChanged(nameof(SelectedTheme));
    }

    private void ApplyTheme()
    {
        var label = SelectedTheme;
        if (string.IsNullOrWhiteSpace(label) || label == Loc.T("CodeThemeDefault"))
        {
            PendingTheme = null;
            return;
        }
        if (label == Loc.T("CodeThemeAuto"))
        {
            var theme = _extensions.Extensions.Where(x => x.Enabled).SelectMany(x => x.Themes).FirstOrDefault();
            if (theme is null) return;
            try { PendingTheme = MonacoThemeConverter.ConvertFile(theme.Path); }
            catch (Exception ex) { Status = Loc.F("CodeThemeFailed", ex.Message); return; }
        }
        else
        {
            var theme = _extensions.Extensions.Where(x => x.Enabled).SelectMany(x => x.Themes)
                .FirstOrDefault(x => x.Label == label);
            if (theme is null) return;
            try { PendingTheme = MonacoThemeConverter.ConvertFile(theme.Path); }
            catch (Exception ex) { Status = Loc.F("CodeThemeFailed", ex.Message); return; }
        }
        ThemePayloadReady?.Invoke(PendingTheme);
    }

    private void RefreshSnippets()
    {
        var groups = new List<object>();
        foreach (var extension in _extensions.Extensions.Where(x => x.Enabled))
        {
            foreach (var snippet in extension.Snippets)
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllBytes(snippet.Path));
                    var entries = new List<object>();
                    foreach (var item in doc.RootElement.EnumerateObject())
                    {
                        if (item.Value.ValueKind != JsonValueKind.Object) continue;
                        object body = string.Empty;
                        if (item.Value.TryGetProperty("body", out var b))
                        {
                            body = b.ValueKind == JsonValueKind.Array
                                ? b.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray()
                                : (object)(b.GetString() ?? string.Empty);
                        }
                        entries.Add(new
                        {
                            prefix = item.Name,
                            body,
                            description = item.Value.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty
                        });
                    }
                    if (entries.Count > 0) groups.Add(new { language = snippet.Language, snippets = entries });
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    // A malformed third-party snippet file must not break the editor.
                }
            }
        }
        PendingSnippets = JsonSerializer.Serialize(groups);
        SnippetsPayloadReady?.Invoke(PendingSnippets);
    }

    private void RaiseCanSave()
    {
        if (SaveCommand is RelayCommand relay) relay.RaiseCanExecuteChanged();
    }
}
