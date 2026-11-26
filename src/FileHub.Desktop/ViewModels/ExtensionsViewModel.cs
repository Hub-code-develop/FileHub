using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using FileHub.Code.Models;
using FileHub.Code.Services;
using FileHub.Desktop.Services;

namespace FileHub.Desktop.ViewModels;

/// <summary>
/// VS Code Extensions-style page. It lists the extensions installed on this machine and shows
/// which declarative contributions FileHub can feed into Monaco. It deliberately does not claim
/// to run extension JavaScript.
/// </summary>
public sealed class ExtensionsViewModel : ViewModelBase
{
    private readonly ExtensionDiscoveryService _service;
    private readonly CodeEditorViewModel _codeEditor;
    private string _search = string.Empty;
    private ExtensionInfo? _selected;
    private string _summary = string.Empty;

    public ExtensionsViewModel(ExtensionDiscoveryService service, CodeEditorViewModel codeEditor)
    {
        _service = service;
        _codeEditor = codeEditor;
        RefreshCommand = new RelayCommand(_ => Refresh());
        ToggleCommand = new RelayCommand(_ => Toggle(), _ => Selected is not null);
        OpenFolderCommand = new RelayCommand(_ => OpenFolder(), _ => Selected is not null);
        CloseCommand = new RelayCommand(_ => Shell?.CloseWorkspacePage());
        Refresh();
    }

    public ShellViewModel? Shell { get; set; }

    public ICommand RefreshCommand { get; }
    public ICommand ToggleCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand CloseCommand { get; }

    public ObservableCollection<ExtensionInfo> Items { get; } = [];

    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) ApplyFilter(); }
    }

    public ExtensionInfo? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                Summary = BuildSummary(value);
                OnPropertyChanged(nameof(ToggleLabel));
                (ToggleCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (OpenFolderCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    public string CountText => Loc.F("ExtCount", Items.Count, _service.Extensions.Count(x => x.Enabled));

    public string ScanText => _service.ScannedRoots.Count == 0
        ? Loc.T("ExtNoRoots")
        : Loc.F("ExtRoots", string.Join(" | ", _service.ScannedRoots));

    public string ToggleLabel => Selected is { Enabled: true } ? Loc.T("ExtDisable") : Loc.T("ExtEnable");

    public void Refresh()
    {
        _service.Refresh();
        ApplyFilter();
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(ScanText));
    }

    private void ApplyFilter()
    {
        var query = Search.Trim();
        Items.Clear();
        foreach (var item in _service.Extensions)
        {
            if (query.Length > 0 &&
                !item.Title.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !item.Id.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !item.Description.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            Items.Add(item);
        }
        if (Selected is not null && !Items.Contains(Selected)) Selected = null;
    }

    private void Toggle()
    {
        if (Selected is not { } current) return;
        var id = current.Id;
        _service.SetEnabled(id, !current.Enabled);
        Refresh();
        Selected = Items.FirstOrDefault(x => x.Id == id);
        OnPropertyChanged(nameof(ToggleLabel));
        _codeEditor.RefreshContributionsCommand.Execute(null);
    }

    private void OpenFolder()
    {
        if (Selected is null || !Directory.Exists(Selected.Folder)) return;
        try
        {
            Process.Start(new ProcessStartInfo(Selected.Folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Summary = Loc.F("ExtOpenFailed", ex.Message);
        }
    }

    private static string BuildSummary(ExtensionInfo? item)
    {
        if (item is null) return Loc.T("ExtSelectHint");
        var lines = new List<string>
        {
            $"{item.Title}  {item.Version}",
            $"{item.Id}",
            string.IsNullOrWhiteSpace(item.Description) ? string.Empty : item.Description,
            string.Empty,
            Loc.F("ExtLanguages", item.Languages.Count),
            Loc.F("ExtThemes", item.Themes.Count),
            Loc.F("ExtSnippets", item.Snippets.Count),
            Loc.F("ExtGrammars", item.Grammars.Count),
            Loc.F("ExtCommands", item.Commands.Count),
            string.Empty,
            Loc.F("ExtFolder", item.Folder)
        };
        return string.Join('\n', lines.Where(x => x is not null));
    }
}
