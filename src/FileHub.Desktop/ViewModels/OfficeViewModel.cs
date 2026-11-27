using System.Collections.ObjectModel;
using System.Windows.Input;
using FileHub.Desktop.Services;
using FileHub.Office.Models;
using FileHub.Office.Services;

namespace FileHub.Desktop.ViewModels;

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

    public OfficeViewModel()
    {
        SaveCommand = new RelayCommand(_ => Save(), _ => HasDocument && IsDirty && !IsBusy);
        SaveAsCommand = new RelayCommand(_ => SaveAs(), _ => HasDocument && !IsBusy);
        ExportHtmlCommand = new RelayCommand(_ => ExportHtml(), _ => HasDocument && !IsBusy);
        CloseCommand = new RelayCommand(_ => Shell?.CloseWorkspacePage());
        UndoCommand = new RelayCommand(_ => Undo(), _ => _undo.Count > 0 && !IsBusy);
        RedoCommand = new RelayCommand(_ => Redo(), _ => _redo.Count > 0 && !IsBusy);
        CreateNewCommand = new RelayCommand(_ => CreateNew());

        SelectTabCommand = new RelayCommand(p => { ActiveTab = p?.ToString() ?? "Home"; RebuildRibbon(); });
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

    public ObservableCollection<WordBlock> Blocks { get; } = new();
    public ObservableCollection<ExcelGridRow> GridRows { get; } = new();
    public ObservableCollection<string> ColumnHeaders { get; } = new();
    public ObservableCollection<ExcelSheet> Sheets { get; } = new();
    public ObservableCollection<PowerPointSlide> Slides { get; } = new();
    public ObservableCollection<string> RibbonTabs { get; } = new();
    public ObservableCollection<RibbonGroup> RibbonGroups { get; } = new();
    public ObservableCollection<string> StyleOptions { get; } = new() { "Normal", "Title", "Heading1", "Heading2", "Heading3" };
    public ObservableCollection<string> FontSizes { get; } = new() { "8", "9", "10", "11", "12", "14", "16", "18", "20", "24", "28", "36", "48", "72" };
    public ObservableCollection<string> FontColors { get; } = new() { "000000", "FFFFFF", "D13438", "2B579A", "107C10", "FFC000", "7030A0" };
    public ObservableCollection<string> FillColors { get; } = new() { "", "FFF2CC", "DDEBF7", "E2EFDA", "FCE4D6" };
    public ObservableCollection<string> LayoutOptions { get; } = new() { "TitleOnly", "Title", "Blank", "TitleAndContent" };
    public ObservableCollection<string> NewDocumentKinds { get; } = new() { "Word", "Excel", "PowerPoint" };

    public bool HasDocument => _document is not null;
    public string FilePath => _document?.Path ?? string.Empty;
    public string FileName => _document?.FileName ?? string.Empty;
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
    public string ActiveTab { get => _activeTab; private set => Set(ref _activeTab, value); }
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
            Status = Loc.F("OfficeOpened", FileName);
            OnPropertyChanged(nameof(HasDocument));
            OnPropertyChanged(nameof(FileName));
            OnPropertyChanged(nameof(FilePath));
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
        RebuildRibbon();
    }

    private void Save()
    {
        if (_document is null || IsBusy) return;
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
            var ext = Path.GetExtension(_document.Path);
            var path = await PickSavePathAsync(FileName, ext);
            if (string.IsNullOrWhiteSpace(path)) return;
            if (!string.Equals(path, _document.Path, StringComparison.OrdinalIgnoreCase))
                File.Copy(_document.Path, path, true);
            _document.Path = path;
            Save();
            OnPropertyChanged(nameof(FileName));
            OnPropertyChanged(nameof(FilePath));
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
        RecalculateSilently();
        UpdateStatistics();
        RefreshFormatState();
        RaiseCommands();
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
        foreach (var tab in tabs) RibbonTabs.Add(tab);
        if (tabs.Length == 0) return;
        if (!tabs.Contains(ActiveTab)) ActiveTab = "Home";

        AddGroup(Loc.T("OfficeGroupFile"),
            Item(Loc.T("OfficeNew"), CreateNewCommand),
            Item(Loc.T("OfficeSave"), SaveCommand),
            Item(Loc.T("OfficeSaveAs"), SaveAsCommand),
            Item(Loc.T("OfficeExportHtml"), ExportHtmlCommand),
            Item(Loc.T("OfficeUndo"), UndoCommand),
            Item(Loc.T("OfficeRedo"), RedoCommand));

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

    private RibbonItem Item(string label, ICommand command, object? parameter = null, bool toggle = false) => new()
    {
        Label = label,
        Command = command,
        Parameter = parameter,
        IsToggle = toggle,
        ToolTip = label
    };

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
            var value = item.Label switch
            {
                "B" => IsBold,
                "I" => IsItalic,
                "U" => IsUnderline,
                _ => false
            };
            if (item.IsChecked != value) item.IsChecked = value;
        }
    }

    private static int ToIndex(object? parameter) =>
        parameter is int i ? i : int.TryParse(parameter?.ToString(), out var parsed) ? parsed : 0;

    private Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    public void SetConfirmHandler(Func<string, string, Task<bool>> handler) => ConfirmAsync = handler;
}
