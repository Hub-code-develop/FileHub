using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using FileHub.Desktop.Services;
using FileHub.Office.Models;
using FileHub.Office.Services;

namespace FileHub.Desktop.ViewModels;

/// <summary>
/// One ribbon tab. <see cref="Key"/> is the stable identifier that filters groups; <see cref="Label"/>
/// is localized, because the ribbon used to show English tab names inside a Chinese window.
/// </summary>
public sealed class RibbonTab
{
    public RibbonTab(string key)
    {
        Key = key;
        Label = key switch
        {
            "File" => Loc.T("OfficeTabFile"),
            "Home" => Loc.T("OfficeTabHome"),
            "Insert" => Loc.T("OfficeTabInsert"),
            "Layout" => Loc.T("OfficeTabLayout"),
            "Review" => Loc.T("OfficeTabReview"),
            "View" => Loc.T("OfficeTabView"),
            "Formulas" => Loc.T("OfficeTabFormulas"),
            "Data" => Loc.T("OfficeTabData"),
            "Design" => Loc.T("OfficeTabDesign"),
            _ => key
        };
    }

    public string Key { get; }
    public string Label { get; }
    public override string ToString() => Label;
}

/// <summary>One button in the Office ribbon.</summary>
public sealed class RibbonItem : ViewModelBase
{
    public string Label { get; init; } = string.Empty;
    public string ToolTip { get; init; } = string.Empty;
    public ICommand Command { get; init; } = new RelayCommand(_ => { });
    public object? Parameter { get; init; }
    public bool IsToggle { get; init; }
    public bool IsEnabled { get; init; } = true;

    private bool _isChecked;
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }
}

/// <summary>A labelled group of ribbon buttons.</summary>
public sealed class RibbonGroup
{
    public string Name { get; init; } = string.Empty;
    public ObservableCollection<RibbonItem> Items { get; } = new();
}

/// <summary>One rendered row of the spreadsheet grid (row header + cells).</summary>
public sealed class ExcelGridRow
{
    public string Header { get; init; } = string.Empty;
    public IReadOnlyList<ExcelCell> Cells { get; init; } = Array.Empty<ExcelCell>();
}

/// <summary>
/// Complete state for the built-in Office workspace: Word, Excel and PowerPoint editing,
/// ribbon commands, undo/redo, formulas, find/replace and file operations.
/// </summary>
public sealed class OfficeViewModel : ViewModelBase
{
    private readonly Stack<OfficeDocument> _undo = new();
    private readonly Stack<OfficeDocument> _redo = new();
    private OfficeDocument? _document;
    private OfficeDocumentKind _kind;
    private string _status = string.Empty;
    private bool _isDirty;
    private bool _isBusy;
    private string _activeTab = "Home";
    private string _newFileName = string.Empty;
    private string _newDocumentKind = "Word";
    private string _selectedStyle = "Normal";
    private string _selectedFontSize = "11";
    private string _selectedColor = "000000";
    private string _selectedLayout = "TitleOnly";
    private string _searchText = string.Empty;
    private string _replaceText = string.Empty;
    private bool _matchCase;
    private string _findResult = string.Empty;
    private string _formulaText = string.Empty;
    private string _cellReference = string.Empty;
    private string _selectionSummary = string.Empty;
    private string _notesText = string.Empty;
    private int _activeSheetIndex;
    private int _activeSlideIndex;
    private int _pages;
    private int _words;
    private int _characters;
    private int _zoom = 100;
    private bool _isBold;
    private bool _isItalic;
    private bool _isUnderline;
    private bool _isRecording;
    private bool _isAdvancedPanelOpen;
    private int _advancedPanelIndex;
    private MacroRecording? _currentMacro;
    private readonly List<string> _lastParagraphTexts = new();
    private readonly Dictionary<ICommand, string> _commandKeys = new();
    private readonly Dictionary<string, ICommand> _commandsByKey = new();

    public OfficeViewModel()
    {
        SaveCommand = new RelayCommand(_ => Save(), _ => HasDocument && IsDirty && !IsBusy);
        SaveAsCommand = new RelayCommand(_ => SaveAs(), _ => HasDocument && !IsBusy);
        ExportHtmlCommand = new RelayCommand(_ => ExportHtml(), _ => HasDocument && !IsBusy);
        CloseCommand = new RelayCommand(_ => Shell?.CloseWorkspacePage());
        UndoCommand = new RelayCommand(_ => Undo(), _ => _undo.Count > 0 && !IsBusy);
        RedoCommand = new RelayCommand(_ => Redo(), _ => _redo.Count > 0 && !IsBusy);
        CreateNewCommand = new RelayCommand(_ => CreateNew());

        SelectTabCommand = new RelayCommand(p =>
        {
            ActiveTab = p is RibbonTab tab ? tab.Key : p?.ToString() ?? "Home";
            RebuildRibbon();
        });
        SelectSheetCommand = new RelayCommand(p => SelectSheet(ToIndex(p)));
        SelectSlideCommand = new RelayCommand(p => SelectSlide(p is PowerPointSlide slide ? Slides.IndexOf(slide) : ToIndex(p)));

        AddParagraphCommand = new RelayCommand(_ => AddParagraph());
        DeleteParagraphCommand = new RelayCommand(_ => DeleteParagraph());
        AddTableCommand = new RelayCommand(_ => AddTable());
        InsertImageCommand = new RelayCommand(_ => InsertImage());
        InsertHyperlinkCommand = new RelayCommand(_ => InsertHyperlink());
        ToggleBoldCommand = new RelayCommand(_ => ToggleBold());
        ToggleItalicCommand = new RelayCommand(_ => ToggleItalic());
        ToggleUnderlineCommand = new RelayCommand(_ => ToggleUnderline());
        SetStyleCommand = new RelayCommand(p => SetStyle(p?.ToString() ?? "Normal"));
        SetFontSizeCommand = new RelayCommand(p => SetFontSize(p?.ToString() ?? "11"));
        SetFontColorCommand = new RelayCommand(p => SetFontColor(p?.ToString() ?? "000000"));
        SetAlignmentCommand = new RelayCommand(p => SetAlignment(p?.ToString() ?? "Left"));
        SetListCommand = new RelayCommand(p => SetList(p?.ToString() ?? "None"));
        FindNextCommand = new RelayCommand(_ => FindNext());
        ReplaceAllCommand = new RelayCommand(_ => ReplaceAll());

        SetFormulaCommand = new RelayCommand(_ => ApplyFormula());
        InsertRowCommand = new RelayCommand(_ => InsertRow());
        DeleteRowCommand = new RelayCommand(_ => DeleteRow());
        InsertColumnCommand = new RelayCommand(_ => InsertColumn());
        DeleteColumnCommand = new RelayCommand(_ => DeleteColumn());
        AddSheetCommand = new RelayCommand(_ => AddSheet());
        DeleteSheetCommand = new RelayCommand(_ => DeleteSheet());
        RenameSheetCommand = new RelayCommand(_ => RenameSheet());
        SetCellFillCommand = new RelayCommand(p => SetCellFill(p?.ToString() ?? string.Empty));
        SetCellAlignmentCommand = new RelayCommand(p => SetCellAlignment(p?.ToString() ?? "Left"));
        InsertFunctionCommand = new RelayCommand(p => InsertFunction(p?.ToString() ?? "SUM"));
        RecalculateCommand = new RelayCommand(_ => Recalculate());

        AddSlideCommand = new RelayCommand(_ => AddSlide());
        DuplicateSlideCommand = new RelayCommand(_ => DuplicateSlide());
        DeleteSlideCommand = new RelayCommand(_ => DeleteSlide());
        MoveSlideCommand = new RelayCommand(p => MoveSlide(p is int delta ? delta : int.Parse(p?.ToString() ?? "0")));
        AddTextBoxCommand = new RelayCommand(_ => AddTextBox());
        SetLayoutCommand = new RelayCommand(p => SetLayout(p?.ToString() ?? "TitleOnly"));

        ZoomInCommand = new RelayCommand(_ => Zoom = Math.Min(240, Zoom + 10));
        ZoomOutCommand = new RelayCommand(_ => Zoom = Math.Max(50, Zoom - 10));
        ZoomResetCommand = new RelayCommand(_ => Zoom = 100);

        ToggleTrackChangesCommand = new RelayCommand(_ => ToggleTrackChanges());
        AcceptRevisionCommand = new RelayCommand(p => AcceptRevision(p as WordRevision));
        RejectRevisionCommand = new RelayCommand(p => RejectRevision(p as WordRevision));
        AcceptAllRevisionsCommand = new RelayCommand(_ => AcceptAllRevisions());
        RejectAllRevisionsCommand = new RelayCommand(_ => RejectAllRevisions());
        ApplyNumberingCommand = new RelayCommand(p => ApplyNumbering(p?.ToString() ?? "2:0"));
        PromoteListCommand = new RelayCommand(_ => ChangeListLevel(-1));
        DemoteListCommand = new RelayCommand(_ => ChangeListLevel(1));
        InsertChartCommand = new RelayCommand(p => InsertChart(p?.ToString() ?? "Column"));
        DeleteChartCommand = new RelayCommand(p => DeleteChart(p as OfficeChart));
        CreatePivotCommand = new RelayCommand(_ => CreatePivot());
        RecalculatePivotCommand = new RelayCommand(p => RecalculatePivot(p as PivotTableModel));
        ExtractObjectCommand = new RelayCommand(p => ExtractObject(p as OfficeEmbeddedObject));
        ToggleMacroRecordingCommand = new RelayCommand(_ => ToggleMacroRecording());
        RunMacroCommand = new RelayCommand(p => RunMacro(p as MacroRecording));
        DeleteMacroCommand = new RelayCommand(p => DeleteMacro(p as MacroRecording));
        ToggleAdvancedPanelCommand = new RelayCommand(_ => IsAdvancedPanelOpen = !IsAdvancedPanelOpen);

        Register("New", CreateNewCommand);
        Register("Save", SaveCommand);
        Register("SaveAs", SaveAsCommand);
        Register("ExportHtml", ExportHtmlCommand);
        Register("Undo", UndoCommand);
        Register("Redo", RedoCommand);
        Register("AddParagraph", AddParagraphCommand);
        Register("DeleteParagraph", DeleteParagraphCommand);
        Register("AddTable", AddTableCommand);
        Register("InsertImage", InsertImageCommand);
        Register("InsertHyperlink", InsertHyperlinkCommand);
        Register("Bold", ToggleBoldCommand);
        Register("Italic", ToggleItalicCommand);
        Register("Underline", ToggleUnderlineCommand);
        Register("Find", FindNextCommand);
        Register("Replace", ReplaceAllCommand);
        Register("InsertRow", InsertRowCommand);
        Register("DeleteRow", DeleteRowCommand);
        Register("InsertColumn", InsertColumnCommand);
        Register("DeleteColumn", DeleteColumnCommand);
        Register("AddSheet", AddSheetCommand);
        Register("DeleteSheet", DeleteSheetCommand);
        Register("RenameSheet", RenameSheetCommand);
        Register("Recalculate", RecalculateCommand);
        Register("AddSlide", AddSlideCommand);
        Register("DuplicateSlide", DuplicateSlideCommand);
        Register("DeleteSlide", DeleteSlideCommand);
        Register("AddTextBox", AddTextBoxCommand);
        Register("ZoomIn", ZoomInCommand);
        Register("ZoomOut", ZoomOutCommand);
    }

    // Cross-page services supplied by the view (no Window dependency in the ViewModel).
    public Func<string, string, string, string, Task<string?>>? PromptAsync { get; set; }
    public Func<string, string, Task>? ShowInfoAsync { get; set; }
    public Func<Task<string?>>? PickImagePathAsync { get; set; }
    public Func<string, string, Task<string?>>? PickSavePathAsync { get; set; }

    public ShellViewModel? Shell { get; set; }

    public ICommand SaveCommand { get; }
    public ICommand SaveAsCommand { get; }
    public ICommand ExportHtmlCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand CreateNewCommand { get; }
    public ICommand SelectTabCommand { get; }
    public ICommand SelectSheetCommand { get; }
    public ICommand SelectSlideCommand { get; }

    public ICommand AddParagraphCommand { get; }
    public ICommand DeleteParagraphCommand { get; }
    public ICommand AddTableCommand { get; }
    public ICommand InsertImageCommand { get; }
    public ICommand InsertHyperlinkCommand { get; }
    public ICommand ToggleBoldCommand { get; }
    public ICommand ToggleItalicCommand { get; }
    public ICommand ToggleUnderlineCommand { get; }
    public ICommand SetStyleCommand { get; }
    public ICommand SetFontSizeCommand { get; }
    public ICommand SetFontColorCommand { get; }
    public ICommand SetAlignmentCommand { get; }
    public ICommand SetListCommand { get; }
    public ICommand FindNextCommand { get; }
    public ICommand ReplaceAllCommand { get; }

    public ICommand SetFormulaCommand { get; }
    public ICommand InsertRowCommand { get; }
    public ICommand DeleteRowCommand { get; }
    public ICommand InsertColumnCommand { get; }
    public ICommand DeleteColumnCommand { get; }
    public ICommand AddSheetCommand { get; }
    public ICommand DeleteSheetCommand { get; }
    public ICommand RenameSheetCommand { get; }
    public ICommand SetCellFillCommand { get; }
    public ICommand SetCellAlignmentCommand { get; }
    public ICommand InsertFunctionCommand { get; }
    public ICommand RecalculateCommand { get; }

    public ICommand AddSlideCommand { get; }
    public ICommand DuplicateSlideCommand { get; }
    public ICommand DeleteSlideCommand { get; }
    public ICommand MoveSlideCommand { get; }
    public ICommand AddTextBoxCommand { get; }
    public ICommand SetLayoutCommand { get; }

    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand ZoomResetCommand { get; }

    public ICommand ToggleTrackChangesCommand { get; }
    public ICommand AcceptRevisionCommand { get; }
    public ICommand RejectRevisionCommand { get; }
    public ICommand AcceptAllRevisionsCommand { get; }
    public ICommand RejectAllRevisionsCommand { get; }
    public ICommand ApplyNumberingCommand { get; }
    public ICommand PromoteListCommand { get; }
    public ICommand DemoteListCommand { get; }
    public ICommand InsertChartCommand { get; }
    public ICommand DeleteChartCommand { get; }
    public ICommand CreatePivotCommand { get; }
    public ICommand RecalculatePivotCommand { get; }
    public ICommand ExtractObjectCommand { get; }
    public ICommand ToggleMacroRecordingCommand { get; }
    public ICommand RunMacroCommand { get; }
    public ICommand DeleteMacroCommand { get; }
    public ICommand ToggleAdvancedPanelCommand { get; }

    public ObservableCollection<WordBlock> Blocks { get; } = new();
    public ObservableCollection<ExcelGridRow> GridRows { get; } = new();
    public ObservableCollection<string> ColumnHeaders { get; } = new();
    public ObservableCollection<ExcelSheet> Sheets { get; } = new();
    public ObservableCollection<PowerPointSlide> Slides { get; } = new();
    public ObservableCollection<RibbonTab> RibbonTabs { get; } = new();
    public ObservableCollection<RibbonGroup> RibbonGroups { get; } = new();
    public ObservableCollection<string> StyleOptions { get; } = new() { "Normal", "Title", "Heading1", "Heading2", "Heading3" };
    public ObservableCollection<string> FontSizes { get; } = new() { "8", "9", "10", "11", "12", "14", "16", "18", "20", "24", "28", "36", "48", "72" };
    public ObservableCollection<string> FontColors { get; } = new() { "000000", "FFFFFF", "D13438", "2B579A", "107C10", "FFC000", "7030A0" };
    public ObservableCollection<string> FillColors { get; } = new() { "", "FFF2CC", "DDEBF7", "E2EFDA", "FCE4D6" };
    public ObservableCollection<string> LayoutOptions { get; } = new() { "TitleOnly", "Title", "Blank", "TitleAndContent" };
    public ObservableCollection<string> NewDocumentKinds { get; } = new() { "Word", "Excel", "PowerPoint" };
    public ObservableCollection<PivotAggregation> PivotAggregations { get; } = new()
    {
        PivotAggregation.Sum, PivotAggregation.Count, PivotAggregation.Average,
        PivotAggregation.Min, PivotAggregation.Max
    };
    public ObservableCollection<MacroSecurityLevel> MacroSecurityLevels { get; } = new()
    {
        MacroSecurityLevel.Disabled, MacroSecurityLevel.Prompt, MacroSecurityLevel.Enabled
    };
    public ObservableCollection<WordRevision> Revisions { get; } = new();
    public ObservableCollection<NumberingDefinition> NumberingDefinitions { get; } = new();
    public ObservableCollection<OfficeChart> Charts { get; } = new();
    public ObservableCollection<PivotTableModel> PivotTables { get; } = new();
    public ObservableCollection<OfficeEmbeddedObject> EmbeddedObjects { get; } = new();
    public ObservableCollection<MacroModule> MacroModules { get; } = new();
    public ObservableCollection<MacroRecording> MacroRecordings { get; } = new();

    public bool HasDocument => _document is not null;
    public string FilePath => _document is { IsDecoded: true } ? _document.SourcePath : _document?.Path ?? string.Empty;
    public string FileName => _document?.DisplayFileName ?? string.Empty;
    public bool IsDecodedSource => _document?.IsDecoded ?? false;
    public string SourceFormat => _document?.SourceFormat ?? string.Empty;
    public bool IsWord => Kind == OfficeDocumentKind.Word;
    public bool IsExcel => Kind == OfficeDocumentKind.Excel;
    public bool IsPowerPoint => Kind == OfficeDocumentKind.PowerPoint;
    public bool CanEdit => HasDocument;

    public string DocumentTypeLabel => Kind switch
    {
        OfficeDocumentKind.Word => Loc.T("OfficeWord"),
        OfficeDocumentKind.Excel => Loc.T("OfficeExcel"),
        OfficeDocumentKind.PowerPoint => Loc.T("OfficePowerPoint"),
        _ => string.Empty
    };

    public OfficeDocumentKind Kind
    {
        get => _kind;
        private set
        {
            if (Set(ref _kind, value))
            {
                OnPropertyChanged(nameof(IsWord));
                OnPropertyChanged(nameof(IsExcel));
                OnPropertyChanged(nameof(IsPowerPoint));
                OnPropertyChanged(nameof(CanEdit));
                OnPropertyChanged(nameof(DocumentTypeLabel));
            }
        }
    }

    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool IsDirty { get => _isDirty; private set { if (Set(ref _isDirty, value)) RaiseCommands(); } }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public string ActiveTab
    {
        get => _activeTab;
        private set
        {
            if (Set(ref _activeTab, value)) OnPropertyChanged(nameof(ActiveTabLabel));
        }
    }

    /// <summary>Localized ribbon-tab caption for the title strip ("开始" instead of "Home").</summary>
    public string ActiveTabLabel => RibbonTabs.FirstOrDefault(t => t.Key == ActiveTab)?.Label ?? ActiveTab;
    public string NewFileName { get => _newFileName; set => Set(ref _newFileName, value); }
    public string NewDocumentKind { get => _newDocumentKind; set => Set(ref _newDocumentKind, value); }
    public string SelectedStyle { get => _selectedStyle; set { if (Set(ref _selectedStyle, value)) SetStyle(value); } }
    public string SelectedFontSize { get => _selectedFontSize; set { if (Set(ref _selectedFontSize, value)) SetFontSize(value); } }
    public string SelectedColor { get => _selectedColor; set { if (Set(ref _selectedColor, value)) SetFontColor(value); } }
    public string SelectedLayout { get => _selectedLayout; set { if (Set(ref _selectedLayout, value)) SetLayout(value); } }
    public string SearchText { get => _searchText; set => Set(ref _searchText, value); }
    public string ReplaceText { get => _replaceText; set => Set(ref _replaceText, value); }
    public bool MatchCase { get => _matchCase; set => Set(ref _matchCase, value); }
    public string FindResult { get => _findResult; private set => Set(ref _findResult, value); }
    public string FormulaText { get => _formulaText; set => Set(ref _formulaText, value); }
    public string CellReference { get => _cellReference; private set => Set(ref _cellReference, value); }
    public string SelectionSummary { get => _selectionSummary; private set => Set(ref _selectionSummary, value); }
    public string NotesText { get => _notesText; set { if (Set(ref _notesText, value)) ApplyNotes(); } }
    public int Pages { get => _pages; private set => Set(ref _pages, value); }
    public int Words { get => _words; private set => Set(ref _words, value); }
    public int Characters { get => _characters; private set => Set(ref _characters, value); }
    public int Zoom { get => _zoom; set => Set(ref _zoom, value); }
    public bool IsBold { get => _isBold; set { if (Set(ref _isBold, value)) ApplyBold(value); } }
    public bool IsItalic { get => _isItalic; set { if (Set(ref _isItalic, value)) ApplyItalic(value); } }
    public bool IsUnderline { get => _isUnderline; set { if (Set(ref _isUnderline, value)) ApplyUnderline(value); } }

    public bool TrackChanges
    {
        get => _document?.TrackChangesEnabled ?? false;
        set
        {
            if (_document is null || _document.TrackChangesEnabled == value) return;
            _document.TrackChangesEnabled = value;
            OnPropertyChanged();
            RebuildRibbon();
        }
    }

    public bool IsAdvancedPanelOpen { get => _isAdvancedPanelOpen; set => Set(ref _isAdvancedPanelOpen, value); }
    public int AdvancedPanelIndex { get => _advancedPanelIndex; set => Set(ref _advancedPanelIndex, value); }
    public bool IsRecording { get => _isRecording; private set { if (Set(ref _isRecording, value)) OnPropertyChanged(nameof(RecordingLabel)); } }
    public string RecordingLabel => IsRecording ? Loc.T("OfficeMacroStop") : Loc.T("OfficeMacroRecord");
    public string VbaWarning => _document?.MacroProject.Warning ?? string.Empty;
    public bool HasVbaProject => _document?.MacroProject.HasVbaProject ?? false;
    public string PivotRange { get; set; } = "A1:D20";
    public string PivotRowField { get; set; } = string.Empty;
    public string PivotColumnField { get; set; } = string.Empty;
    public string PivotValueField { get; set; } = string.Empty;
    public PivotAggregation PivotAggregation { get; set; } = PivotAggregation.Sum;
    public MacroSecurityLevel MacroSecurity
    {
        get => MacroService.SecurityLevel;
        set { MacroService.SecurityLevel = value; OnPropertyChanged(); }
    }

    public string TrackChangesLabel => TrackChanges ? Loc.T("OfficeTrackingOn") : Loc.T("OfficeTrackingOff");
    public WordRevision? SelectedRevision { get; set; }
    public WordParagraph? SelectedParagraph { get; set; }
    public ExcelCell? SelectedCell { get; set; }
    public PowerPointShape? SelectedShape { get; set; }

    public ExcelSheet? ActiveSheet => Sheets.ElementAtOrDefault(_activeSheetIndex);
    public PowerPointSlide? ActiveSlide => Slides.ElementAtOrDefault(_activeSlideIndex);

    // ---------------------------------------------------------------- file lifecycle

    public async Task OpenAsync(string path)
    {
        IsBusy = true;
        try
        {
            var doc = await Task.Run(() => OfficeDocumentService.Load(path));
            _document = doc;
            _undo.Clear();
            _redo.Clear();
            Kind = doc.Kind;
            ActiveTab = "Home";
            SyncFromDocument();
            IsDirty = false;
            Status = doc.IsDecoded
                ? Loc.F("OfficeWpsDecoded", doc.SourceFormat)
                : Loc.F("OfficeOpened", FileName);
            OnPropertyChanged(nameof(HasDocument));
            OnPropertyChanged(nameof(FileName));
            OnPropertyChanged(nameof(FilePath));
            OnPropertyChanged(nameof(IsDecodedSource));
            OnPropertyChanged(nameof(SourceFormat));
            RaiseCommands();
        }
        catch (Exception ex)
        {
            Status = Loc.F("OfficeOpenFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SyncFromDocument()
    {
        Blocks.Clear();
        Sheets.Clear();
        Slides.Clear();
        GridRows.Clear();
        ColumnHeaders.Clear();
        if (_document is null) return;

        foreach (var block in _document.Blocks) Blocks.Add(block);
        foreach (var sheet in _document.Sheets) Sheets.Add(sheet);
        foreach (var slide in _document.Slides) Slides.Add(slide);

        if (IsExcel && Sheets.Count > 0) SelectSheet(0);
        if (IsPowerPoint && Slides.Count > 0) SelectSlide(0);
        SelectedParagraph = Blocks.OfType<WordParagraph>().FirstOrDefault();
        SelectedCell = null;
        SelectedShape = null;
        RecalculateSilently();
        UpdateStatistics();
        RefreshFormatState();
        SyncAdvancedCollections();
        RebuildRibbon();
    }

    private void Save()
    {
        if (_document is null || IsBusy) return;
        if (_document.IsDecoded)
        {
            // A decoded WPS binary is backed by a temporary OOXML file; never overwrite the
            // original WPS container with OOXML bytes. Force an explicit Save As instead.
            SaveAs();
            return;
        }
        try
        {
            OfficeDocumentService.Save(_document);
            IsDirty = false;
            Status = Loc.F("OfficeSavedFile", FileName);
        }
        catch (Exception ex)
        {
            Status = Loc.F("OfficeSaveFailed", ex.Message);
        }
    }

    private async void SaveAs()
    {
        if (_document is null || PickSavePathAsync is null) return;
        try
        {
            var decoded = _document.IsDecoded;
            var ext = Path.GetExtension(_document.Path);
            var suggested = decoded && !string.IsNullOrEmpty(_document.SourcePath)
                ? WpsFormatService.SuggestedFileName(_document.SourcePath)
                : FileName;
            var path = await PickSavePathAsync(suggested, ext);
            if (string.IsNullOrWhiteSpace(path)) return;
            if (!string.Equals(path, _document.Path, StringComparison.OrdinalIgnoreCase))
                File.Copy(_document.Path, path, true);
            _document.Path = path;
            _document.SourcePath = string.Empty;
            _document.SourceFormat = string.Empty;
            Save();
            OnPropertyChanged(nameof(FileName));
            OnPropertyChanged(nameof(FilePath));
            OnPropertyChanged(nameof(IsDecodedSource));
            OnPropertyChanged(nameof(SourceFormat));
        }
        catch (Exception ex)
        {
            Status = Loc.F("OfficeSaveFailed", ex.Message);
        }
    }

    private async void ExportHtml()
    {
        if (_document is null || PickSavePathAsync is null) return;
        try
        {
            var path = await PickSavePathAsync(Path.ChangeExtension(FileName, ".html"), ".html");
            if (string.IsNullOrWhiteSpace(path)) return;
            await File.WriteAllTextAsync(path, OfficeTools.ExportHtml(_document));
            Status = Loc.F("OfficeExported", path);
        }
        catch (Exception ex)
        {
            Status = Loc.F("OfficeSaveFailed", ex.Message);
        }
    }

    private async void CreateNew()
    {
        var name = string.IsNullOrWhiteSpace(NewFileName) ? "Document" : NewFileName.Trim();
        var kind = NewDocumentKind switch
        {
            "Excel" => OfficeDocumentKind.Excel,
            "PowerPoint" => OfficeDocumentKind.PowerPoint,
            _ => OfficeDocumentKind.Word
        };
        var ext = kind switch
        {
            OfficeDocumentKind.Excel => ".xlsx",
            OfficeDocumentKind.PowerPoint => ".pptx",
            _ => ".docx"
        };
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = Path.Combine(dir, $"{name}{ext}");
        var counter = 1;
        while (File.Exists(path)) path = Path.Combine(dir, $"{name} ({counter++}){ext}");

        try
        {
            await Task.Run(() => OfficeDocumentService.CreateNew(path, kind));
            await OpenAsync(path);
        }
        catch (Exception ex)
        {
            Status = Loc.F("OfficeCreateFailed", ex.Message);
        }
    }

    // ---------------------------------------------------------------- undo / redo

    private void PushUndo()
    {
        if (_document is null) return;
        _undo.Push(OfficeTools.Clone(_document));
        while (_undo.Count > 40) Trim(_undo);
        _redo.Clear();
        RaiseCommands();
    }

    private static void Trim(Stack<OfficeDocument> stack)
    {
        var items = stack.ToList();
        stack.Clear();
        foreach (var item in items.TakeLast(40)) stack.Push(item);
    }

    private void Undo()
    {
        if (_undo.Count == 0 || _document is null) return;
        _redo.Push(OfficeTools.Clone(_document));
        _document = _undo.Pop();
        SyncFromDocument();
        IsDirty = true;
        Status = Loc.T("OfficeUndo");
        RaiseCommands();
    }

    private void Redo()
    {
        if (_redo.Count == 0 || _document is null) return;
        _undo.Push(OfficeTools.Clone(_document));
        _document = _redo.Pop();
        SyncFromDocument();
        IsDirty = true;
        Status = Loc.T("OfficeRedo");
        RaiseCommands();
    }

    /// <summary>Called by the view when a text box gains focus, before any edit happens.</summary>
    public void BeginEdit() => PushUndo();

    public void MarkDirty()
    {
        IsDirty = true;
        if (IsWord && _document is not null)
        {
            var paragraphs = _document.Paragraphs.ToList();
            if (_lastParagraphTexts.Count == paragraphs.Count)
            {
                for (var i = 0; i < paragraphs.Count; i++)
                {
                    var current = paragraphs[i].PlainText;
                    if (_lastParagraphTexts[i] == current) continue;
                    RevisionService.RecordChange(_document, i, _lastParagraphTexts[i], current);
                    _lastParagraphTexts[i] = current;
                }
            }
            WordNumberingService.RefreshLabels(_document);
        }
        SyncAdvancedCollections();
        RecalculateSilently();
        UpdateStatistics();
        RefreshFormatState();
        RaiseCommands();
    }

    private void SyncAdvancedCollections()
    {
        Revisions.Clear();
        NumberingDefinitions.Clear();
        Charts.Clear();
        PivotTables.Clear();
        EmbeddedObjects.Clear();
        MacroModules.Clear();
        MacroRecordings.Clear();
        if (_document is null) return;
        foreach (var revision in _document.Revisions) Revisions.Add(revision);
        foreach (var definition in _document.NumberingDefinitions) NumberingDefinitions.Add(definition);
        foreach (var chart in _document.Charts) Charts.Add(chart);
        foreach (var pivot in _document.PivotTables) PivotTables.Add(pivot);
        foreach (var embedded in _document.EmbeddedObjects) EmbeddedObjects.Add(embedded);
        foreach (var module in _document.MacroProject.Modules) MacroModules.Add(module);
        foreach (var recording in MacroService.LoadRecordings()) MacroRecordings.Add(recording);
        _lastParagraphTexts.Clear();
        _lastParagraphTexts.AddRange(_document.Paragraphs.Select(p => p.PlainText));
        OnPropertyChanged(nameof(TrackChanges));
        OnPropertyChanged(nameof(TrackChangesLabel));
        OnPropertyChanged(nameof(HasVbaProject));
        OnPropertyChanged(nameof(VbaWarning));
    }

    private void RaiseCommands()
    {
        foreach (var command in new[] { SaveCommand, UndoCommand, RedoCommand })
            (command as RelayCommand)?.RaiseCanExecuteChanged();
    }

    // ---------------------------------------------------------------- Word editing

    private void AddParagraph()
    {
        if (_document is null || !IsWord) return;
        PushUndo();
        var paragraph = new WordParagraph();
        var index = SelectedParagraph is null ? _document.Blocks.Count : _document.Blocks.IndexOf(SelectedParagraph) + 1;
        _document.Blocks.Insert(Math.Max(0, index), paragraph);
        Blocks.Insert(Math.Max(0, index), paragraph);
        SelectedParagraph = paragraph;
        MarkDirty();
    }

    private void DeleteParagraph()
    {
        if (_document is null || SelectedParagraph is null || !IsWord) return;
        PushUndo();
        _document.Blocks.Remove(SelectedParagraph);
        Blocks.Remove(SelectedParagraph);
        SelectedParagraph = Blocks.OfType<WordParagraph>().FirstOrDefault();
        MarkDirty();
    }

    private void AddTable()
    {
        if (_document is null || !IsWord) return;
        PushUndo();
        var table = WordTable.Create(3, 3);
        var index = SelectedParagraph is null ? _document.Blocks.Count : _document.Blocks.IndexOf(SelectedParagraph) + 1;
        _document.Blocks.Insert(Math.Max(0, index), table);
        Blocks.Insert(Math.Max(0, index), table);
        MarkDirty();
    }

    private async void InsertImage()
    {
        if (_document is null || !IsWord || PickImagePathAsync is null) return;
        try
        {
            var path = await PickImagePathAsync();
            if (string.IsNullOrWhiteSpace(path)) return;
            PushUndo();
            var image = new WordImage
            {
                Path = path,
                Data = await File.ReadAllBytesAsync(path),
                ContentType = ContentTypeFor(path),
                WidthPx = 480,
                HeightPx = 320
            };
            var index = SelectedParagraph is null ? _document.Blocks.Count : _document.Blocks.IndexOf(SelectedParagraph) + 1;
            _document.Blocks.Insert(Math.Max(0, index), image);
            Blocks.Insert(Math.Max(0, index), image);
            MarkDirty();
        }
        catch (Exception ex)
        {
            Status = Loc.F("OfficeOpenFailed", ex.Message);
        }
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        _ => "image/png"
    };

    private async void InsertHyperlink()
    {
        if (!IsWord || PromptAsync is null) return;
        var url = await PromptAsync(Loc.T("OfficeInsertLink"), Loc.T("OfficeInsertLinkHint"), "https://", Loc.T("OfficeInsert"));
        if (string.IsNullOrWhiteSpace(url)) return;
        PushUndo();
        var paragraph = SelectedParagraph ?? new WordParagraph();
        if (SelectedParagraph is null && _document is not null)
        {
            _document.Blocks.Add(paragraph);
            Blocks.Add(paragraph);
        }
        paragraph.Runs.Add(new WordRun { Text = url, Hyperlink = url });
        paragraph.NotifyChanged(nameof(WordParagraph.PlainText));
        MarkDirty();
    }

    private void ToggleBold() => ApplyBold(!IsBold);
    private void ToggleItalic() => ApplyItalic(!IsItalic);
    private void ToggleUnderline() => ApplyUnderline(!IsUnderline);

    private void ApplyBold(bool value)
    {
        if (!IsWord || SelectedParagraph is null) return;
        PushUndo();
        foreach (var run in SelectedParagraph.Runs) run.IsBold = value;
        SelectedParagraph.NotifyChanged(nameof(WordParagraph.PlainText));
        IsBold = value;
        MarkDirty();
    }

    private void ApplyItalic(bool value)
    {
        if (!IsWord || SelectedParagraph is null) return;
        PushUndo();
        foreach (var run in SelectedParagraph.Runs) run.IsItalic = value;
        SelectedParagraph.NotifyChanged(nameof(WordParagraph.PlainText));
        IsItalic = value;
        MarkDirty();
    }

    private void ApplyUnderline(bool value)
    {
        if (!IsWord || SelectedParagraph is null) return;
        PushUndo();
        foreach (var run in SelectedParagraph.Runs) run.IsUnderline = value;
        SelectedParagraph.NotifyChanged(nameof(WordParagraph.PlainText));
        IsUnderline = value;
        MarkDirty();
    }

    private void SetStyle(string style)
    {
        if (!IsWord || SelectedParagraph is null || SelectedParagraph.Style == style) return;
        PushUndo();
        SelectedParagraph.Style = style;
        MarkDirty();
    }

    private void SetFontSize(string text)
    {
        if (!double.TryParse(text, out var size)) return;
        if (IsWord && SelectedParagraph is not null)
        {
            PushUndo();
            foreach (var run in SelectedParagraph.Runs) run.FontSize = size;
            SelectedParagraph.NotifyChanged(nameof(WordParagraph.PlainText));
        }
        else if (IsPowerPoint && SelectedShape is not null)
        {
            PushUndo();
            SelectedShape.FontSize = size;
            SelectedShape.NotifyChanged(nameof(PowerPointShape.Text));
        }
        MarkDirty();
    }

    private void SetFontColor(string hex)
    {
        if (!IsWord || SelectedParagraph is null) return;
        PushUndo();
        foreach (var run in SelectedParagraph.Runs) run.ColorHex = hex;
        SelectedParagraph.NotifyChanged(nameof(WordParagraph.PlainText));
        MarkDirty();
    }

    private void SetAlignment(string value)
    {
        var alignment = value switch
        {
            "Center" => WordAlignment.Center,
            "Right" => WordAlignment.Right,
            "Justify" => WordAlignment.Justify,
            _ => WordAlignment.Left
        };
        if (IsWord && SelectedParagraph is not null)
        {
            PushUndo();
            SelectedParagraph.Alignment = alignment;
        }
        else if (IsExcel && SelectedCell is not null)
        {
            PushUndo();
            SelectedCell.Alignment = alignment;
        }
        MarkDirty();
    }

    private void SetList(string value)
    {
        if (!IsWord || SelectedParagraph is null) return;
        PushUndo();
        SelectedParagraph.ListKind = value switch
        {
            "Bullet" => WordListKind.Bullet,
            "Number" => WordListKind.Number,
            _ => WordListKind.None
        };
        MarkDirty();
    }

    private void FindNext()
    {
        if (!IsWord || _document is null) return;
        FindResult = Loc.F("OfficeFindCount", OfficeTools.FindInWord(_document, SearchText, MatchCase));
    }

    private void ReplaceAll()
    {
        if (!IsWord || _document is null || string.IsNullOrEmpty(SearchText)) return;
        PushUndo();
        var count = OfficeTools.ReplaceInWord(_document, SearchText, ReplaceText, MatchCase);
        Blocks.Clear();
        foreach (var block in _document.Blocks) Blocks.Add(block);
        FindResult = Loc.F("OfficeReplaceCount", count);
        MarkDirty();
    }

    // ---------------------------------------------------------------- Excel editing

    public void SelectCell(ExcelCell cell)
    {
        SelectedCell = cell;
        CellReference = cell.Reference;
        FormulaText = cell.EditingText;
        var sheet = ActiveSheet;
        if (sheet is not null)
        {
            var values = sheet.Cells.Where(c => c.HasFormula || !string.IsNullOrEmpty(c.Display))
                .Select(c => double.TryParse(c.Display, out var d) ? (double?)d : null)
                .Where(v => v.HasValue).Select(v => v!.Value).ToList();
            SelectionSummary = values.Count == 0 ? string.Empty
                : $"{Loc.T("OfficeSum")}: {values.Sum():0.##}  {Loc.T("OfficeAverage")}: {values.Average():0.##}  {Loc.T("OfficeCount")}: {values.Count}";
        }
        RefreshFormatState();
    }

    private void ApplyFormula()
    {
        if (!IsExcel || SelectedCell is null) return;
        PushUndo();
        var text = FormulaText.Trim();
        if (text.StartsWith('='))
        {
            SelectedCell.Formula = text;
            SelectedCell.Display = FormulaEngine.Evaluate(text, ActiveSheet ?? new ExcelSheet(), Sheets);
        }
        else
        {
            SelectedCell.Formula = string.Empty;
            SelectedCell.Display = text;
        }
        SelectedCell.NotifyChanged(nameof(ExcelCell.Display));
        MarkDirty();
    }

    private void InsertFunction(string name)
    {
        if (!IsExcel || SelectedCell is null) return;
        PushUndo();
        SelectedCell.Formula = $"={name}(A1:A10)";
        SelectedCell.Display = FormulaEngine.Evaluate(SelectedCell.Formula, ActiveSheet ?? new ExcelSheet(), Sheets);
        FormulaText = SelectedCell.Formula;
        SelectedCell.NotifyChanged(nameof(ExcelCell.Display));
        MarkDirty();
    }

    private void Recalculate() { RecalculateSilently(); MarkDirty(); }

    private void RecalculateSilently()
    {
        if (_document is null || !IsExcel) return;
        foreach (var sheet in _document.Sheets)
        foreach (var cell in sheet.Cells.Where(c => c.HasFormula))
        {
            cell.Display = FormulaEngine.Evaluate(cell.Formula, sheet, _document.Sheets);
            cell.NotifyChanged(nameof(ExcelCell.Display));
        }
        foreach (var row in GridRows)
        foreach (var cell in row.Cells)
            if (cell.HasFormula) cell.NotifyChanged(nameof(ExcelCell.Display));
    }

    private void SelectSheet(int index)
    {
        if (Sheets.Count == 0) return;
        _activeSheetIndex = Math.Clamp(index, 0, Sheets.Count - 1);
        OnPropertyChanged(nameof(ActiveSheet));
        BuildGrid();
    }

    private void BuildGrid()
    {
        var sheet = ActiveSheet;
        GridRows.Clear();
        ColumnHeaders.Clear();
        if (sheet is null) return;
        for (var c = 1; c <= sheet.ColumnCount; c++)
        {
            ColumnHeaders.Add(ExcelSheet.CellReference(1, c).TrimEnd('1'));
            while (sheet.ColumnWidths.Count < c) sheet.ColumnWidths.Add(110);
        }
        for (var r = 1; r <= sheet.RowCount; r++)
        {
            var cells = new List<ExcelCell>();
            for (var c = 1; c <= sheet.ColumnCount; c++) cells.Add(sheet.Ensure(r, c));
            GridRows.Add(new ExcelGridRow { Header = r.ToString(), Cells = cells });
        }
    }

    private void InsertRow()
    {
        var sheet = ActiveSheet;
        if (sheet is null) return;
        PushUndo();
        foreach (var cell in sheet.Cells.Where(c => c.Row >= (SelectedCell?.Row ?? sheet.RowCount)).ToList()) cell.Row++;
        foreach (var cell in sheet.Cells) cell.Reference = ExcelSheet.CellReference(cell.Row, cell.Column);
        sheet.RowCount++;
        BuildGrid();
        MarkDirty();
    }

    private void DeleteRow()
    {
        var sheet = ActiveSheet;
        if (sheet is null) return;
        var row = SelectedCell?.Row ?? sheet.RowCount;
        PushUndo();
        foreach (var cell in sheet.Cells.Where(c => c.Row == row).ToList()) sheet.Cells.Remove(cell);
        foreach (var cell in sheet.Cells.Where(c => c.Row > row)) cell.Row--;
        foreach (var cell in sheet.Cells) cell.Reference = ExcelSheet.CellReference(cell.Row, cell.Column);
        sheet.RowCount = Math.Max(1, sheet.RowCount - 1);
        BuildGrid();
        MarkDirty();
    }

    private void InsertColumn()
    {
        var sheet = ActiveSheet;
        if (sheet is null) return;
        PushUndo();
        var column = SelectedCell?.Column ?? sheet.ColumnCount;
        foreach (var cell in sheet.Cells.Where(c => c.Column >= column).ToList()) cell.Column++;
        foreach (var cell in sheet.Cells) cell.Reference = ExcelSheet.CellReference(cell.Row, cell.Column);
        sheet.ColumnCount++;
        sheet.ColumnWidths.Insert(Math.Max(0, column - 1), 110);
        BuildGrid();
        MarkDirty();
    }

    private void DeleteColumn()
    {
        var sheet = ActiveSheet;
        if (sheet is null) return;
        var column = SelectedCell?.Column ?? sheet.ColumnCount;
        PushUndo();
        foreach (var cell in sheet.Cells.Where(c => c.Column == column).ToList()) sheet.Cells.Remove(cell);
        foreach (var cell in sheet.Cells.Where(c => c.Column > column)) cell.Column--;
        foreach (var cell in sheet.Cells) cell.Reference = ExcelSheet.CellReference(cell.Row, cell.Column);
        sheet.ColumnCount = Math.Max(1, sheet.ColumnCount - 1);
        if (sheet.ColumnWidths.Count >= column) sheet.ColumnWidths.RemoveAt(column - 1);
        BuildGrid();
        MarkDirty();
    }

    private async void AddSheet()
    {
        if (_document is null || PromptAsync is null) return;
        var name = await PromptAsync(Loc.T("OfficeAddSheet"), Loc.T("OfficeSheetNameHint"), $"Sheet{Sheets.Count + 1}", Loc.T("OfficeInsert"));
        if (string.IsNullOrWhiteSpace(name)) return;
        if (Sheets.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))) return;
        PushUndo();
        var sheet = new ExcelSheet { Name = name.Trim() };
        _document.Sheets.Add(sheet);
        Sheets.Add(sheet);
        SelectSheet(Sheets.Count - 1);
        MarkDirty();
    }

    private async void DeleteSheet()
    {
        var sheet = ActiveSheet;
        if (sheet is null || _document is null || Sheets.Count <= 1) return;
        if (ConfirmAsync is null || !await ConfirmAsync(Loc.T("OfficeDeleteSheet"), Loc.F("OfficeDeleteSheetHint", sheet.Name))) return;
        PushUndo();
        var index = _activeSheetIndex;
        _document.Sheets.Remove(sheet);
        Sheets.Remove(sheet);
        SelectSheet(Math.Min(index, Sheets.Count - 1));
        MarkDirty();
    }

    private async void RenameSheet()
    {
        var sheet = ActiveSheet;
        if (sheet is null || PromptAsync is null) return;
        var name = await PromptAsync(Loc.T("OfficeRenameSheet"), Loc.T("OfficeSheetNameHint"), sheet.Name, Loc.T("OfficeSave"));
        if (string.IsNullOrWhiteSpace(name)) return;
        PushUndo();
        sheet.Name = name.Trim();
        OnPropertyChanged(nameof(ActiveSheet));
        MarkDirty();
    }

    private void SetCellFill(string hex)
    {
        if (SelectedCell is null) return;
        PushUndo();
        SelectedCell.FillColor = hex;
        SelectedCell.NotifyChanged(nameof(ExcelCell.FillColor));
        MarkDirty();
    }

    private void SetCellAlignment(string value)
    {
        if (SelectedCell is null) return;
        PushUndo();
        SelectedCell.Alignment = value switch
        {
            "Center" => WordAlignment.Center,
            "Right" => WordAlignment.Right,
            _ => WordAlignment.Left
        };
        MarkDirty();
    }

    // ---------------------------------------------------------------- PowerPoint editing

    private void SelectSlide(int index)
    {
        if (Slides.Count == 0) return;
        _activeSlideIndex = Math.Clamp(index, 0, Slides.Count - 1);
        var slide = ActiveSlide;
        SelectedShape = slide?.Shapes.FirstOrDefault();
        Set(ref _notesText, slide?.Notes ?? string.Empty);
        OnPropertyChanged(nameof(NotesText));
        OnPropertyChanged(nameof(ActiveSlide));
        RefreshFormatState();
    }

    private void AddSlide()
    {
        if (_document is null || !IsPowerPoint) return;
        PushUndo();
        var slide = new PowerPointSlide
        {
            Number = Slides.Count + 1,
            Layout = SelectedLayout,
            Shapes =
            {
                new PowerPointShape { Name = "Title", Text = Loc.T("OfficeClickTitle"), IsTitle = true, X = 60, Y = 40, Width = 840, Height = 90, FontSize = 36, IsBold = true },
                new PowerPointShape { Name = "Content", Text = Loc.T("OfficeClickText"), X = 60, Y = 150, Width = 840, Height = 330, FontSize = 20 }
            }
        };
        _document.Slides.Add(slide);
        Slides.Add(slide);
        SelectSlide(Slides.Count - 1);
        MarkDirty();
    }

    private void DuplicateSlide()
    {
        var slide = ActiveSlide;
        if (slide is null || _document is null) return;
        PushUndo();
        var copy = slide.Clone();
        copy.Number = Slides.Count + 1;
        _document.Slides.Add(copy);
        Slides.Add(copy);
        SelectSlide(Slides.Count - 1);
        MarkDirty();
    }

    private void DeleteSlide()
    {
        var slide = ActiveSlide;
        if (slide is null || _document is null || Slides.Count <= 1) return;
        PushUndo();
        var index = _activeSlideIndex;
        _document.Slides.Remove(slide);
        Slides.Remove(slide);
        for (var i = 0; i < Slides.Count; i++) Slides[i].Number = i + 1;
        SelectSlide(Math.Min(index, Slides.Count - 1));
        MarkDirty();
    }

    private void MoveSlide(int delta)
    {
        var slide = ActiveSlide;
        if (slide is null || _document is null) return;
        var target = _activeSlideIndex + delta;
        if (target < 0 || target >= Slides.Count) return;
        PushUndo();
        Slides.Move(_activeSlideIndex, target);
        _document.Slides.Clear();
        foreach (var item in Slides) _document.Slides.Add(item);
        for (var i = 0; i < Slides.Count; i++) Slides[i].Number = i + 1;
        SelectSlide(target);
        MarkDirty();
    }

    private void AddTextBox()
    {
        var slide = ActiveSlide;
        if (slide is null) return;
        PushUndo();
        var shape = new PowerPointShape
        {
            Name = $"Text Box {slide.Shapes.Count + 1}",
            Text = Loc.T("OfficeClickText"),
            X = 120, Y = 220, Width = 400, Height = 90, FontSize = 18
        };
        slide.Shapes.Add(shape);
        SelectedShape = shape;
        MarkDirty();
        // Force the slide canvas to re-render its shape list.
        var index = _activeSlideIndex;
        OnPropertyChanged(nameof(ActiveSlide));
        SelectSlide(index);
    }

    private void SetLayout(string layout)
    {
        var slide = ActiveSlide;
        if (slide is null) return;
        PushUndo();
        slide.Layout = layout;
        MarkDirty();
    }

    private void ApplyNotes()
    {
        var slide = ActiveSlide;
        if (slide is null || slide.Notes == NotesText) return;
        PushUndo();
        slide.Notes = NotesText;
        MarkDirty();
    }

    // ---------------------------------------------------------------- shared helpers

    /// <summary>Called by the view after selection changes so ribbon toggles reflect the caret.</summary>
    public void RefreshSelectionState() => RefreshFormatState();

    private void RefreshFormatState()
    {
        if (IsWord && SelectedParagraph is not null)
        {
            var run = SelectedParagraph.Runs.FirstOrDefault();
            _isBold = run?.IsBold ?? false;
            _isItalic = run?.IsItalic ?? false;
            _isUnderline = run?.IsUnderline ?? false;
            _selectedStyle = SelectedParagraph.Style;
            if (run?.FontSize > 0) _selectedFontSize = ((int)run.FontSize).ToString();
            _selectedColor = string.IsNullOrEmpty(run?.ColorHex) ? "000000" : run!.ColorHex;
        }
        else if (IsExcel && SelectedCell is not null)
        {
            _isBold = SelectedCell.IsBold;
            _isItalic = SelectedCell.IsItalic;
            _isUnderline = SelectedCell.IsUnderline;
        }
        else if (IsPowerPoint && SelectedShape is not null)
        {
            _isBold = SelectedShape.IsBold;
            _selectedFontSize = ((int)SelectedShape.FontSize).ToString();
        }
        OnPropertyChanged(nameof(IsBold));
        OnPropertyChanged(nameof(IsItalic));
        OnPropertyChanged(nameof(IsUnderline));
        OnPropertyChanged(nameof(SelectedStyle));
        OnPropertyChanged(nameof(SelectedFontSize));
        OnPropertyChanged(nameof(SelectedColor));
        UpdateRibbonChecks();
    }

    private void UpdateStatistics()
    {
        if (_document is null) return;
        var stats = OfficeTools.WordStatistics(_document);
        Pages = stats.Pages;
        Words = stats.Words;
        Characters = stats.Characters;
        if (IsPowerPoint && ActiveSlide is not null)
            Set(ref _selectedLayout, string.IsNullOrEmpty(ActiveSlide.Layout) ? "TitleOnly" : ActiveSlide.Layout);
    }

    private void RebuildRibbon()
    {
        RibbonTabs.Clear();
        RibbonGroups.Clear();
        var tabs = Kind switch
        {
            OfficeDocumentKind.Word => new[] { "File", "Home", "Insert", "Layout", "Review", "View" },
            OfficeDocumentKind.Excel => new[] { "File", "Home", "Insert", "Formulas", "Data", "View" },
            OfficeDocumentKind.PowerPoint => new[] { "File", "Home", "Insert", "Design", "View" },
            _ => Array.Empty<string>()
        };
        foreach (var tab in tabs) RibbonTabs.Add(new RibbonTab(tab));
        OnPropertyChanged(nameof(ActiveTabLabel));
        if (tabs.Length == 0) return;
        if (!tabs.Contains(ActiveTab)) ActiveTab = "Home";

        AddGroup(Loc.T("OfficeGroupFile"),
            Item(Loc.T("OfficeNew"), CreateNewCommand),
            Item(Loc.T("OfficeSave"), SaveCommand),
            Item(Loc.T("OfficeSaveAs"), SaveAsCommand),
            Item(Loc.T("OfficeExportHtml"), ExportHtmlCommand),
            Item(Loc.T("OfficeUndo"), UndoCommand),
            Item(Loc.T("OfficeRedo"), RedoCommand),
            Item(Loc.T("OfficeAdvancedPanel"), ToggleAdvancedPanelCommand));

        switch (Kind, ActiveTab)
        {
            case (OfficeDocumentKind.Word, "Home"):
                AddGroup(Loc.T("OfficeGroupParagraph"),
                    Item(Loc.T("OfficeAddParagraph"), AddParagraphCommand),
                    Item(Loc.T("OfficeDeleteParagraph"), DeleteParagraphCommand),
                    Item(Loc.T("OfficeAlignLeft"), SetAlignmentCommand, "Left"),
                    Item(Loc.T("OfficeAlignCenter"), SetAlignmentCommand, "Center"),
                    Item(Loc.T("OfficeAlignRight"), SetAlignmentCommand, "Right"),
                    Item(Loc.T("OfficeAlignJustify"), SetAlignmentCommand, "Justify"));
                AddGroup(Loc.T("OfficeGroupFont"),
                    Item("B", ToggleBoldCommand, toggle: true),
                    Item("I", ToggleItalicCommand, toggle: true),
                    Item("U", ToggleUnderlineCommand, toggle: true),
                    Item(Loc.T("OfficeBullets"), SetListCommand, "Bullet"),
                    Item(Loc.T("OfficeNumbering"), SetListCommand, "Number"),
                    Item(Loc.T("OfficeNoList"), SetListCommand, "None"));
                AddGroup(Loc.T("OfficeMultilevel"),
                    Item("1", ApplyNumberingCommand, "2:0"),
                    Item("1.1", ApplyNumberingCommand, "2:1"),
                    Item("1.1.1", ApplyNumberingCommand, "2:2"),
                    Item("←", PromoteListCommand),
                    Item("→", DemoteListCommand));
                break;
            case (OfficeDocumentKind.Word, "Insert"):
                AddGroup(Loc.T("OfficeTabInsert"),
                    Item(Loc.T("OfficeAddTable"), AddTableCommand),
                    Item(Loc.T("OfficeInsertImage"), InsertImageCommand),
                    Item(Loc.T("OfficeInsertLink"), InsertHyperlinkCommand));
                break;
            case (OfficeDocumentKind.Word, "Layout"):
                AddGroup(Loc.T("OfficeTabLayout"),
                    Item(Loc.T("OfficeAlignLeft"), SetAlignmentCommand, "Left"),
                    Item(Loc.T("OfficeAlignCenter"), SetAlignmentCommand, "Center"),
                    Item(Loc.T("OfficeAlignRight"), SetAlignmentCommand, "Right"));
                break;
            case (OfficeDocumentKind.Word, "Review"):
                AddGroup(Loc.T("OfficeTabReview"),
                    Item(Loc.T("OfficeFind"), FindNextCommand),
                    Item(Loc.T("OfficeReplace"), ReplaceAllCommand));
                AddGroup(Loc.T("OfficeRevisions"),
                    Item(Loc.T("OfficeTrackChanges"), ToggleTrackChangesCommand, toggle: true),
                    Item(Loc.T("OfficeAccept"), AcceptRevisionCommand),
                    Item(Loc.T("OfficeReject"), RejectRevisionCommand),
                    Item(Loc.T("OfficeAcceptAll"), AcceptAllRevisionsCommand),
                    Item(Loc.T("OfficeRejectAll"), RejectAllRevisionsCommand));
                break;

            case (OfficeDocumentKind.Excel, "Home"):
                AddGroup(Loc.T("OfficeGroupCells"),
                    Item(Loc.T("OfficeInsertRow"), InsertRowCommand),
                    Item(Loc.T("OfficeDeleteRow"), DeleteRowCommand),
                    Item(Loc.T("OfficeInsertColumn"), InsertColumnCommand),
                    Item(Loc.T("OfficeDeleteColumn"), DeleteColumnCommand));
                AddGroup(Loc.T("OfficeGroupFont"),
                    Item("B", ToggleBoldCommand, toggle: true),
                    Item("I", ToggleItalicCommand, toggle: true),
                    Item("U", ToggleUnderlineCommand, toggle: true),
                    Item(Loc.T("OfficeAlignLeft"), SetCellAlignmentCommand, "Left"),
                    Item(Loc.T("OfficeAlignCenter"), SetCellAlignmentCommand, "Center"),
                    Item(Loc.T("OfficeAlignRight"), SetCellAlignmentCommand, "Right"));
                break;
            case (OfficeDocumentKind.Excel, "Insert"):
                AddGroup(Loc.T("OfficeTabInsert"),
                    Item(Loc.T("OfficeAddSheet"), AddSheetCommand),
                    Item(Loc.T("OfficeRenameSheet"), RenameSheetCommand),
                    Item(Loc.T("OfficeInsertRow"), InsertRowCommand),
                    Item(Loc.T("OfficeInsertColumn"), InsertColumnCommand));
                AddGroup(Loc.T("OfficeCharts"),
                    Item(Loc.T("OfficeChartColumn"), InsertChartCommand, "Column"),
                    Item(Loc.T("OfficeChartBar"), InsertChartCommand, "Bar"),
                    Item(Loc.T("OfficeChartLine"), InsertChartCommand, "Line"),
                    Item(Loc.T("OfficeChartPie"), InsertChartCommand, "Pie"),
                    Item(Loc.T("OfficeChartArea"), InsertChartCommand, "Area"));
                break;
            case (OfficeDocumentKind.Excel, "Formulas"):
                AddGroup(Loc.T("OfficeTabFormulas"),
                    Item("SUM", InsertFunctionCommand, "SUM"),
                    Item("AVERAGE", InsertFunctionCommand, "AVERAGE"),
                    Item("MIN", InsertFunctionCommand, "MIN"),
                    Item("MAX", InsertFunctionCommand, "MAX"),
                    Item("IF", InsertFunctionCommand, "IF"),
                    Item(Loc.T("OfficeRecalculate"), RecalculateCommand));
                break;
            case (OfficeDocumentKind.Excel, "Data"):
                AddGroup(Loc.T("OfficeTabData"),
                    Item(Loc.T("OfficeAddSheet"), AddSheetCommand),
                    Item(Loc.T("OfficeRenameSheet"), RenameSheetCommand),
                    Item(Loc.T("OfficeDeleteSheet"), DeleteSheetCommand),
                    Item(Loc.T("OfficeRecalculate"), RecalculateCommand));
                AddGroup(Loc.T("OfficePivotTables"),
                    Item(Loc.T("OfficePivotCreate"), CreatePivotCommand),
                    Item(Loc.T("OfficePivotRefresh"), RecalculatePivotCommand));
                break;

            case (OfficeDocumentKind.PowerPoint, "Home"):
                AddGroup(Loc.T("OfficeGroupSlides"),
                    Item(Loc.T("OfficeAddSlide"), AddSlideCommand),
                    Item(Loc.T("OfficeDuplicateSlide"), DuplicateSlideCommand),
                    Item(Loc.T("OfficeDeleteSlide"), DeleteSlideCommand),
                    Item("↑", MoveSlideCommand, -1),
                    Item("↓", MoveSlideCommand, 1));
                AddGroup(Loc.T("OfficeGroupFont"),
                    Item("B", ToggleBoldCommand, toggle: true),
                    Item(Loc.T("OfficeAddTextBox"), AddTextBoxCommand));
                break;
            case (OfficeDocumentKind.PowerPoint, "Insert"):
                AddGroup(Loc.T("OfficeTabInsert"),
                    Item(Loc.T("OfficeAddTextBox"), AddTextBoxCommand),
                    Item(Loc.T("OfficeAddSlide"), AddSlideCommand));
                break;
            case (OfficeDocumentKind.PowerPoint, "Design"):
                AddGroup(Loc.T("OfficeTabDesign"),
                    Item(Loc.T("OfficeLayoutTitle"), SetLayoutCommand, "TitleOnly"),
                    Item(Loc.T("OfficeLayoutBlank"), SetLayoutCommand, "Blank"));
                break;
        }

        AddGroup(Loc.T("OfficeTabView"),
            Item("+", ZoomInCommand),
            Item("−", ZoomOutCommand),
            Item("100%", ZoomResetCommand));
        UpdateRibbonChecks();
    }

    private void Register(string key, ICommand command)
    {
        _commandKeys[command] = key;
        _commandsByKey[key] = command;
    }

    private RibbonItem Item(string label, ICommand command, object? parameter = null, bool toggle = false)
    {
        var key = _commandKeys.TryGetValue(command, out var registered) ? registered : label;
        return new RibbonItem
        {
            Label = label,
            Command = new RecordingCommand(key, parameter, command, this),
            Parameter = parameter,
            IsToggle = toggle,
            ToolTip = label
        };
    }

    private void RecordAction(string key, object? parameter)
    {
        if (!IsRecording || _currentMacro is null) return;
        _currentMacro.Actions.Add(new MacroAction
        {
            Kind = key,
            Parameter = parameter?.ToString() ?? string.Empty
        });
    }

    private void ToggleTrackChanges()
    {
        if (_document is null) return;
        _document.TrackChangesEnabled = !_document.TrackChangesEnabled;
        OnPropertyChanged(nameof(TrackChanges));
        OnPropertyChanged(nameof(TrackChangesLabel));
        RebuildRibbon();
        Status = _document.TrackChangesEnabled ? Loc.T("OfficeTrackingOn") : Loc.T("OfficeTrackingOff");
    }

    private void AcceptRevision(WordRevision? revision)
    {
        if (_document is null || revision is null) return;
        RevisionService.Accept(_document, revision);
        MarkDirty();
    }

    private void RejectRevision(WordRevision? revision)
    {
        if (_document is null || revision is null) return;
        RevisionService.Reject(_document, revision);
        SyncFromDocument();
        MarkDirty();
    }

    private void AcceptAllRevisions()
    {
        if (_document is null) return;
        RevisionService.AcceptAll(_document);
        MarkDirty();
    }

    private void RejectAllRevisions()
    {
        if (_document is null) return;
        RevisionService.RejectAll(_document);
        SyncFromDocument();
        MarkDirty();
    }

    private void ApplyNumbering(string spec)
    {
        if (!IsWord || _document is null || SelectedParagraph is null) return;
        var parts = spec.Split(':');
        var numberId = int.TryParse(parts.ElementAtOrDefault(0), out var id) ? id : 2;
        var level = int.TryParse(parts.ElementAtOrDefault(1), out var l) ? l : 0;
        PushUndo();
        WordNumberingService.Apply(_document, SelectedParagraph, numberId, level);
        MarkDirty();
    }

    private void ChangeListLevel(int delta)
    {
        if (!IsWord || SelectedParagraph is null) return;
        PushUndo();
        if (SelectedParagraph.ListKind == WordListKind.None)
            SelectedParagraph.ListKind = WordListKind.Number;
        SelectedParagraph.ListLevel = Math.Clamp(SelectedParagraph.ListLevel + delta, 0, 8);
        if (_document is not null) WordNumberingService.RefreshLabels(_document);
        MarkDirty();
    }

    private void InsertChart(string type)
    {
        if (!IsExcel || _document is null || ActiveSheet is null) return;
        var cells = ActiveSheet.Cells.Where(c => !string.IsNullOrEmpty(c.Display)).ToList();
        if (cells.Count == 0) { Status = Loc.T("OfficeChartNoData"); return; }
        var minRow = cells.Min(c => c.Row);
        var maxRow = cells.Max(c => c.Row);
        var minCol = cells.Min(c => c.Column);
        var maxCol = cells.Max(c => c.Column);
        var range = $"{ExcelSheet.CellReference(minRow, minCol)}:{ExcelSheet.CellReference(maxRow, maxCol)}";
        var parsed = Enum.TryParse<OfficeChartType>(type, true, out var chartType) ? chartType : OfficeChartType.Column;
        var chart = ChartService.CreateFromRange(_document, ActiveSheet.Name, range, parsed,
            $"{parsed} {_document.Charts.Count + 1}");
        _document.Charts.Add(chart);
        MarkDirty();
        Status = Loc.F("OfficeChartCreated", chart.Name);
    }

    private void DeleteChart(OfficeChart? chart)
    {
        if (_document is null || chart is null) return;
        ChartService.Delete(_document, chart);
        _document.Charts.Remove(chart);
        MarkDirty();
    }

    private void CreatePivot()
    {
        if (!IsExcel || _document is null || ActiveSheet is null) return;
        var pivot = PivotService.CreateFromRange(_document, ActiveSheet.Name, PivotRange,
            PivotRowField, PivotColumnField, PivotValueField, PivotAggregation);
        _document.PivotTables.Add(pivot);
        MarkDirty();
        Status = Loc.F("OfficePivotCreated", pivot.Name);
    }

    private void RecalculatePivot(PivotTableModel? pivot)
    {
        if (_document is null || pivot is null) return;
        PivotService.Recalculate(pivot, _document);
        MarkDirty();
    }

    private async void ExtractObject(OfficeEmbeddedObject? embedded)
    {
        if (embedded is null || ShowInfoAsync is null) return;
        try
        {
            var path = OleObjectService.Extract(embedded);
            await ShowInfoAsync(Loc.T("OfficeObjects"), $"{embedded.Name}\n{path}");
        }
        catch (Exception ex)
        {
            Status = Loc.F("OfficeOpenFailed", ex.Message);
        }
    }

    private void ToggleMacroRecording()
    {
        if (IsRecording)
        {
            IsRecording = false;
            if (_currentMacro is { } completed && completed.Actions.Count > 0)
            {
                MacroService.SaveRecording(completed);
                Status = Loc.F("OfficeMacroSaved", completed.Name);
            }
            _currentMacro = null;
            SyncAdvancedCollections();
            return;
        }
        _currentMacro = new MacroRecording { Name = $"Macro {MacroService.LoadRecordings().Count + 1}" };
        IsRecording = true;
        Status = Loc.T("OfficeMacroRecording");
    }

    private void RunMacro(MacroRecording? macro)
    {
        if (macro is null) return;
        var wasRecording = IsRecording;
        IsRecording = false;
        try
        {
            foreach (var action in macro.Actions)
            {
                if (!_commandsByKey.TryGetValue(action.Kind, out var command)) continue;
                object? parameter = action.Parameter;
                if (int.TryParse(action.Parameter, out var number)) parameter = number;
                if (command.CanExecute(parameter)) command.Execute(parameter);
            }
            Status = Loc.F("OfficeMacroRan", macro.Name);
        }
        finally
        {
            IsRecording = wasRecording;
        }
    }

    private void DeleteMacro(MacroRecording? macro)
    {
        if (macro is null) return;
        MacroService.DeleteRecording(macro.Name);
        SyncAdvancedCollections();
    }

    private sealed class RecordingCommand : ICommand
    {
        private readonly ICommand _inner;
        private readonly OfficeViewModel _owner;
        private readonly string _key;
        private readonly object? _parameter;

        public RecordingCommand(string key, object? parameter, ICommand inner, OfficeViewModel owner)
        {
            _key = key;
            _parameter = parameter;
            _inner = inner;
            _owner = owner;
            inner.CanExecuteChanged += (_, _) => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }

        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => _inner.CanExecute(parameter);
        public void Execute(object? parameter)
        {
            _owner.RecordAction(_key, _parameter ?? parameter);
            _inner.Execute(parameter);
        }
    }

    private void AddGroup(string name, params RibbonItem[] items)
    {
        var group = new RibbonGroup { Name = name };
        foreach (var item in items) group.Items.Add(item);
        RibbonGroups.Add(group);
    }

    private void UpdateRibbonChecks()
    {
        foreach (var group in RibbonGroups)
        foreach (var item in group.Items)
        {
            if (!item.IsToggle) continue;
            // Match on the command, not the label: labels are localized ("修订模式"), so the old
            // label switch never matched Track changes and its toggle looked permanently off.
            var value = item.Command switch
            {
                _ when ReferenceEquals(item.Command, ToggleBoldCommand) => IsBold,
                _ when ReferenceEquals(item.Command, ToggleItalicCommand) => IsItalic,
                _ when ReferenceEquals(item.Command, ToggleUnderlineCommand) => IsUnderline,
                _ when ReferenceEquals(item.Command, ToggleTrackChangesCommand) => TrackChanges,
                _ => item.IsChecked
            };
            if (item.IsChecked != value) item.IsChecked = value;
        }
    }

    private static int ToIndex(object? parameter) =>
        parameter is int i ? i : int.TryParse(parameter?.ToString(), out var parsed) ? parsed : 0;

    private Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    public void SetConfirmHandler(Func<string, string, Task<bool>> handler) => ConfirmAsync = handler;
}
