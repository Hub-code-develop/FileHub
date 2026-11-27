using System.Collections.ObjectModel;
using System.Windows.Input;
using FileHub.Desktop.Services;
using FileHub.Office.Models;
using FileHub.Office.Services;

namespace FileHub.Desktop.ViewModels;

/// <summary>
/// State for the built-in Office page. The view renders a Microsoft-Office-style ribbon and the
/// document content; this class owns file I/O and the small set of edits the page supports.
/// </summary>
public sealed class OfficeViewModel : ViewModelBase
{
    private OfficeDocument? _document;
    private OfficeDocumentKind _kind;
    private string _status = string.Empty;
    private bool _isDirty;
    private bool _isBusy;
    private string _newFileName = string.Empty;
    private bool _isBold;
    private bool _isItalic;
    private bool _isUnderline;
    private string _selectedStyle = "Normal";
    private string _activeTab = "Home";

    public OfficeViewModel()
    {
        var save = new RelayCommand(_ => Save(), _ => HasDocument && IsDirty && !IsBusy);
        SaveCommand = save;

        var create = new RelayCommand(_ => CreateNew());
        CreateNewCommand = create;

        CloseCommand = new RelayCommand(_ => Shell?.CloseWorkspacePage());
        ToggleBoldCommand = new RelayCommand(_ => ApplyFormat(p => p.IsBold = !p.IsBold));
        ToggleItalicCommand = new RelayCommand(_ => ApplyFormat(p => p.IsItalic = !p.IsItalic));
        ToggleUnderlineCommand = new RelayCommand(_ => ApplyFormat(p => p.IsUnderline = !p.IsUnderline));
        SetStyleCommand = new RelayCommand(p => ApplyFormat(x => x.Style = p?.ToString() ?? "Normal"));
        SelectTabCommand = new RelayCommand(p => ActiveTab = p?.ToString() ?? "Home");
    }

    public ShellViewModel? Shell { get; set; }

    public ICommand SaveCommand { get; }
    public ICommand CreateNewCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand ToggleBoldCommand { get; }
    public ICommand ToggleItalicCommand { get; }
    public ICommand ToggleUnderlineCommand { get; }
    public ICommand SetStyleCommand { get; }
    public ICommand SelectTabCommand { get; }

    public ObservableCollection<WordParagraph> Paragraphs { get; } = new();
    public ObservableCollection<ExcelSheet> Sheets { get; } = new();
    public ObservableCollection<PowerPointSlide> Slides { get; } = new();
    public ObservableCollection<string> StyleOptions { get; } = new() { "Normal", "Title", "Heading1", "Heading2", "Heading3" };

    public bool HasDocument => _document is not null;
    public string FilePath => _document?.Path ?? string.Empty;
    public string FileName => _document?.FileName ?? string.Empty;
    public string DocumentTypeLabel => Kind switch
    {
        OfficeDocumentKind.Word => Loc.T("OfficeWord"),
        OfficeDocumentKind.Excel => Loc.T("OfficeExcel"),
        OfficeDocumentKind.PowerPoint => Loc.T("OfficePowerPoint"),
        _ => string.Empty
    };
    public bool IsWord => Kind == OfficeDocumentKind.Word;
    public bool IsExcel => Kind == OfficeDocumentKind.Excel;
    public bool IsPowerPoint => Kind == OfficeDocumentKind.PowerPoint;
    public bool CanEdit => Kind is OfficeDocumentKind.Word or OfficeDocumentKind.Excel;

    public OfficeDocumentKind Kind
    {
        get => _kind;
        private set
        {
            if (Set(ref _kind, value))
            {
                OnPropertyChanged(nameof(DocumentTypeLabel));
                OnPropertyChanged(nameof(IsWord));
                OnPropertyChanged(nameof(IsExcel));
                OnPropertyChanged(nameof(IsPowerPoint));
                OnPropertyChanged(nameof(CanEdit));
            }
        }
    }

    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool IsDirty { get => _isDirty; private set { if (Set(ref _isDirty, value)) RaiseSave(); } }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public string NewFileName { get => _newFileName; set => Set(ref _newFileName, value); }
    public string ActiveTab { get => _activeTab; private set => Set(ref _activeTab, value); }

    public bool IsBold { get => _isBold; set { if (Set(ref _isBold, value)) ApplyFormat(p => p.IsBold = value); } }
    public bool IsItalic { get => _isItalic; set { if (Set(ref _isItalic, value)) ApplyFormat(p => p.IsItalic = value); } }
    public bool IsUnderline { get => _isUnderline; set { if (Set(ref _isUnderline, value)) ApplyFormat(p => p.IsUnderline = value); } }

    public string SelectedStyle
    {
        get => _selectedStyle;
        set { if (Set(ref _selectedStyle, value)) ApplyFormat(p => p.Style = value); }
    }

    public WordParagraph? SelectedParagraph { get; set; }
    public ExcelCell? SelectedCell { get; set; }

    /// <summary>Open an Office file; called by Shell when a document is double-clicked.</summary>
    public async Task OpenAsync(string path)
    {
        IsBusy = true;
        try
        {
            var doc = await Task.Run(() => OfficeDocumentService.Load(path));
            _document = doc;
            Kind = doc.Kind;
            Paragraphs.Clear();
            Sheets.Clear();
            Slides.Clear();
            foreach (var p in doc.Paragraphs) Paragraphs.Add(p);
            foreach (var s in doc.Sheets) Sheets.Add(s);
            foreach (var s in doc.Slides) Slides.Add(s);
            IsDirty = false;
            Status = Loc.F("OfficeOpened", FileName);
            OnPropertyChanged(nameof(HasDocument));
            OnPropertyChanged(nameof(FileName));
            OnPropertyChanged(nameof(FilePath));
            RaiseSave();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Save()
    {
        if (_document is null || IsBusy) return;
        try
        {
            OfficeDocumentService.Save(_document);
            IsDirty = false;
            Status = Loc.T("OfficeSaved");
        }
        catch (Exception ex)
        {
            Status = Loc.F("OfficeSaveFailed", ex.Message);
        }
    }

    private void CreateNew()
    {
        var name = string.IsNullOrWhiteSpace(NewFileName) ? "Document" : NewFileName.Trim();
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var ext = ".docx";
        var path = Path.Combine(dir, $"{name}{ext}");
        var counter = 1;
        while (File.Exists(path)) path = Path.Combine(dir, $"{name} ({counter++}){ext}");

        try
        {
            OfficeDocumentService.CreateNew(path, OfficeDocumentKind.Word);
            _ = OpenAsync(path);
        }
        catch (Exception ex)
        {
            Status = Loc.F("OfficeCreateFailed", ex.Message);
        }
    }

    private void ApplyFormat(Action<WordParagraph> action)
    {
        if (!IsWord) return;
        var target = SelectedParagraph ?? Paragraphs.FirstOrDefault();
        if (target is null) return;
        action(target);
        MarkDirty();
    }

    /// <summary>Called by the view when the user edits a paragraph or cell.</summary>
    public void MarkDirty()
    {
        IsDirty = true;
    }

    private void RaiseSave()
    {
        (SaveCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void ReportError(Exception ex)
    {
        Status = Loc.F("OfficeOpenFailed", ex.Message);
    }
}
