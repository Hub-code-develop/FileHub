using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using FileHub.Code.Models;
using FileHub.Code.Services;
using FileHub.Desktop.Services;

namespace FileHub.Desktop.ViewModels;

/// <summary>Which list the Extensions page is showing.</summary>
public enum ExtensionPageMode
{
    Installed,
    Marketplace
}

/// <summary>
/// VS Code Extensions-style page. It lists local extensions and can search the Visual Studio
/// Marketplace to install more. It deliberately does not claim to run extension JavaScript.
/// </summary>
public sealed class ExtensionsViewModel : ViewModelBase
{
    private readonly ExtensionDiscoveryService _service;
    private readonly CodeEditorViewModel _codeEditor;
    private readonly MarketplaceService _marketplace = new();
    private string _search = string.Empty;
    private string _marketplaceSearch = string.Empty;
    private ExtensionInfo? _selected;
    private MarketplaceExtension? _selectedMarketplace;
    private string _summary = string.Empty;
    private string _marketplaceSummary = string.Empty;
    private ExtensionPageMode _mode;
    private bool _isBusy;
    private double _progress;
    private string _status = string.Empty;

    public ExtensionsViewModel(ExtensionDiscoveryService service, CodeEditorViewModel codeEditor)
    {
        _service = service;
        _codeEditor = codeEditor;
        RefreshCommand = new RelayCommand(_ => Refresh());
        ToggleCommand = new RelayCommand(_ => Toggle(), _ => Selected is not null);
        OpenFolderCommand = new RelayCommand(_ => OpenFolder(), _ => Selected is not null);

        ShowInstalledCommand = new RelayCommand(_ => Mode = ExtensionPageMode.Installed);
        ShowMarketplaceCommand = new RelayCommand(_ => Mode = ExtensionPageMode.Marketplace);
        var searchCommand = new AsyncRelayCommand(_ => SearchMarketplaceAsync(), _ => !IsBusy);
        var installCommand = new AsyncRelayCommand(_ => InstallAsync(),
            _ => SelectedMarketplace is { IsCompatible: true, VsixUrl.Length: > 0 } && !IsBusy);
        searchCommand.ErrorHandler = ReportError;
        installCommand.ErrorHandler = ReportError;
        SearchMarketplaceCommand = searchCommand;
        InstallCommand = installCommand;

        UninstallCommand = new RelayCommand(_ => Uninstall(), _ => Selected is not null);
        OpenStorePageCommand = new RelayCommand(_ => OpenUrl(SelectedMarketplace?.MarketplaceUrl),
            _ => SelectedMarketplace is not null);
        OpenLicenseCommand = new RelayCommand(_ => OpenUrl(SelectedMarketplace?.LicenseUrl),
            _ => !string.IsNullOrWhiteSpace(SelectedMarketplace?.LicenseUrl));
        CloseCommand = new RelayCommand(_ => Shell?.CloseWorkspacePage());

        Refresh();
    }

    public ShellViewModel? Shell { get; set; }

    public ICommand RefreshCommand { get; }
    public ICommand ToggleCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand ShowInstalledCommand { get; }
    public ICommand ShowMarketplaceCommand { get; }
    public ICommand SearchMarketplaceCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand UninstallCommand { get; }
    public ICommand OpenStorePageCommand { get; }
    public ICommand OpenLicenseCommand { get; }

    public ObservableCollection<ExtensionInfo> Items { get; } = [];
    public ObservableCollection<MarketplaceExtension> MarketplaceItems { get; } = [];

    public ExtensionPageMode Mode
    {
        get => _mode;
        set
        {
            if (!Set(ref _mode, value)) return;
            OnPropertyChanged(nameof(IsInstalledMode));
            OnPropertyChanged(nameof(IsMarketplaceMode));
            Status = string.Empty;
            if (value == ExtensionPageMode.Marketplace && MarketplaceItems.Count == 0)
                (SearchMarketplaceCommand as AsyncRelayCommand)?.Execute(null);
        }
    }

    public bool IsInstalledMode => Mode == ExtensionPageMode.Installed;
    public bool IsMarketplaceMode => Mode == ExtensionPageMode.Marketplace;

    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) ApplyFilter(); }
    }

    /// <summary>Marketplace query. Press Enter or click Search; no request is sent while typing.</summary>
    public string MarketplaceSearch
    {
        get => _marketplaceSearch;
        set => Set(ref _marketplaceSearch, value);
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
                (UninstallCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public MarketplaceExtension? SelectedMarketplace
    {
        get => _selectedMarketplace;
        set
        {
            if (Set(ref _selectedMarketplace, value))
            {
                MarketplaceSummary = BuildMarketplaceSummary(value);
                (InstallCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (OpenStorePageCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (OpenLicenseCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public string MarketplaceSummary { get => _marketplaceSummary; private set => Set(ref _marketplaceSummary, value); }

    public string CountText => Loc.F("ExtCount", Items.Count, _service.Extensions.Count(x => x.Enabled));
    public string MarketplaceCountText => MarketplaceItems.Count == 0
        ? string.Empty : Loc.F("MktCount", MarketplaceItems.Count);

    public string ScanText => _service.ScannedRoots.Count == 0
        ? Loc.T("ExtNoRoots")
        : Loc.F("ExtRoots", string.Join(" | ", _service.ScannedRoots));

    public string ToggleLabel => Selected is { Enabled: true } ? Loc.T("ExtDisable") : Loc.T("ExtEnable");

    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }

    public string InstallLabel => SelectedMarketplace is { IsCompatible: true }
        ? Loc.T("MktInstall") : Loc.T("MktIncompatible");

    public string PlatformText => Loc.F("MktPlatform", MarketplaceService.CurrentPlatform);

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

    private async Task SearchMarketplaceAsync()
    {
        IsBusy = true;
        Status = Loc.F("MktSearching", MarketplaceSearch.Trim());
        OnPropertyChanged(nameof(MarketplaceCountText));
        try
        {
            var results = await _marketplace.SearchAsync(MarketplaceSearch, 40);
            MarketplaceItems.Clear();
            foreach (var item in results) MarketplaceItems.Add(item);
            SelectedMarketplace = null;
            Status = results.Count == 0 ? Loc.T("MktNoResults") : string.Empty;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(MarketplaceCountText));
        }
    }

    private async Task InstallAsync()
    {
        if (SelectedMarketplace is not { } chosen) return;
        IsBusy = true;
        Progress = 0;
        Status = Loc.F("MktDownloading", chosen.Title);
        try
        {
            var progress = new Progress<double>(value => Progress = value);
            var vsix = await _marketplace.DownloadAsync(chosen, progress);
            try
            {
                Status = Loc.F("MktInstalling", chosen.Title);
                MarketplaceService.Install(vsix, chosen);
            }
            finally
            {
                File.Delete(vsix);
            }

            Refresh();
            _codeEditor.RefreshContributionsCommand.Execute(null);
            Status = Loc.F("MktInstalled", chosen.Title);
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
        }
    }

    private void Uninstall()
    {
        if (Selected is not { } current) return;
        var id = current.Id;
        if (!MarketplaceService.Uninstall(id))
        {
            Status = Loc.T("MktNotManaged");
            return;
        }
        if (current.Enabled) _service.SetEnabled(id, false);
        Refresh();
        _codeEditor.RefreshContributionsCommand.Execute(null);
        Status = Loc.F("MktUninstalled", current.Title);
    }

    private void ReportError(Exception error)
    {
        IsBusy = false;
        Status = Loc.F("MktFailed", error.Message);
    }

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !(Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
              uri.Scheme is "http" or "https")) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            // Opening a browser is best-effort; the URL is still visible in the details pane.
        }
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

    private static string BuildMarketplaceSummary(MarketplaceExtension? item)
    {
        if (item is null) return Loc.T("MktSelectHint");
        var lines = new List<string>
        {
            $"{item.Title}  {item.Version}",
            item.Id,
            string.IsNullOrWhiteSpace(item.Description) ? string.Empty : item.Description,
            string.Empty,
            Loc.F("MktPublisher", item.PublisherDisplayName.Length > 0 ? item.PublisherDisplayName : item.Publisher),
            Loc.F("MktInstalls", item.InstallCount),
            item.RatingCount > 0 ? Loc.F("MktRating", item.Rating, item.RatingCount) : string.Empty,
            Loc.F("MktPlatformDetail", string.IsNullOrWhiteSpace(item.TargetPlatform)
                ? Loc.T("MktUniversal") : item.TargetPlatform),
            string.Empty,
            Loc.T("MktNotice")
        };
        return string.Join('\n', lines.Where(x => !string.IsNullOrWhiteSpace(x)));
    }
}
