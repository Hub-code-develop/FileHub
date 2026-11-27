using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using FileHub.Code.Services;
using FileHub.Desktop.Actions;
using FileHub.Desktop.Services;
using FileHub.Desktop.Views;
using FileHub.Files.Plugin;
using FileHub.Files.Services;
using FileHub.Files.Vfs;

namespace FileHub.Desktop.ViewModels;

/// <summary>
/// Which full-area page is shown on top of the Files workspace. Both pages are document modes
/// opened by a double-click on a matching file: there is no entry button for either of them.
/// Settings and extensions live in the settings dialog instead.
/// </summary>
public enum WorkspacePageKind
{
    Files,
    Code,
    Office
}

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
    private WorkspacePageKind _workspacePage = WorkspacePageKind.Files;
    private ExtensionDiscoveryService? _extensionDiscovery;
    private CodeEditorViewModel? _codeEditor;
    private ExtensionsViewModel? _extensions;
    private OfficeViewModel? _office;
    private QuickLookViewModel? _quickLook;
    private CommandPaletteViewModel? _palette;
    private MainViewModel? _secondPane;
    private bool _isTransferPaneOpen;
    private bool _isDualPaneOpen;

    /// <summary>Paths of the most recently closed tabs, newest first (Files: reopen closed tab).</summary>
    private readonly List<string> _closedTabPaths = [];
    private const int MaxClosedTabs = 20;

    private ShellViewModel()
    {
        NewTabCommand = new RelayCommand(_ => AddTab());
        OpenSettingsCommand = new RelayCommand(_ => _ = SettingsDialog.ShowAsync(this));
        CloseWorkspacePageCommand = new RelayCommand(_ => CloseWorkspacePage());
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

        // Transfers report progress from a worker thread; marshal every property change.
        Transfers = new TransferQueue(action => Dispatcher.UIThread.Post(action))
        {
            ConflictResolver = ResolveConflictAsync
        };
        Undo = new UndoService();

        ToggleTransferPaneCommand = new RelayCommand(_ => IsTransferPaneOpen = !IsTransferPaneOpen);
        ToggleDualPaneCommand = new RelayCommand(_ => ToggleDualPane());
        ToggleQuickLookCommand = new RelayCommand(_ => _ = ToggleQuickLookAsync());
        OpenPaletteCommand = new RelayCommand(_ => Palette.Open());
        UndoCommand = new RelayCommand(_ => _ = StepUndoAsync(undo: true));
        RedoCommand = new RelayCommand(_ => _ = StepUndoAsync(undo: false));
        CancelJobCommand = new RelayCommand(p =>
        {
            if (p is TransferJob job) Transfers.Cancel(job);
        });
        PauseJobCommand = new RelayCommand(p =>
        {
            if (p is not TransferJob job) return;
            if (job.State == TransferJobState.Paused) Transfers.Resume(job);
            else Transfers.Pause(job);
        });
        ClearTransfersCommand = new RelayCommand(_ => Transfers.ClearFinished());

        Commands = new CommandManager(this);
        PluginHost.Instance.Load();
        WatchRuleService.Instance.Start();
    }

    public TransferQueue Transfers { get; }
    public UndoService Undo { get; }
    public QuickLookViewModel QuickLook => _quickLook ??= new QuickLookViewModel();
    public CommandPaletteViewModel Palette => _palette ??= new CommandPaletteViewModel(BuildPaletteItems);

    public bool IsTransferPaneOpen
    {
        get => _isTransferPaneOpen;
        set => Set(ref _isTransferPaneOpen, value);
    }

    public bool IsDualPaneOpen
    {
        get => _isDualPaneOpen;
        private set
        {
            if (Set(ref _isDualPaneOpen, value))
            {
                OnPropertyChanged(nameof(IsFilesAreaVisible));
                RaiseDualPaneState();
            }
        }
    }

    /// <summary>
    /// The second folder pane belongs to the file-manager surface; document mode hides it together
    /// with the tab strip, toolbar and status bar so only the document and the sidebar remain.
    /// </summary>
    public bool IsDualPaneVisible => IsDualPaneOpen && !IsOfficePageOpen;

    /// <summary>
    /// Width of the dual-pane column. A bare "*" column keeps half the window even when its child
    /// is collapsed, so the column is driven by the same flag that hides the pane.
    /// </summary>
    public GridLength DualPaneColumnWidth => IsDualPaneVisible
        ? new GridLength(1, GridUnitType.Star)
        : new GridLength(0);

    private void RaiseDualPaneState()
    {
        OnPropertyChanged(nameof(IsDualPaneVisible));
        OnPropertyChanged(nameof(DualPaneColumnWidth));
    }

    /// <summary>Secondary folder pane; created on first use and kept afterwards.</summary>
    public MainViewModel? SecondPane
    {
        get => _secondPane;
        private set => Set(ref _secondPane, value);
    }

    public bool IsFilesAreaVisible => !IsWorkspacePageOpen;


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
    public ICommand OpenSettingsCommand { get; }
    public ICommand CloseWorkspacePageCommand { get; }
    public ICommand ToggleTransferPaneCommand { get; }
    public ICommand ToggleDualPaneCommand { get; }
    public ICommand ToggleQuickLookCommand { get; }
    public ICommand OpenPaletteCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand CancelJobCommand { get; }
    public ICommand PauseJobCommand { get; }
    public ICommand ClearTransfersCommand { get; }

    /// <summary>Full-area overlay: Files, the Monaco editor, or the Office document page.</summary>
    public WorkspacePageKind WorkspacePage
    {
        get => _workspacePage;
        private set
        {
            if (!Set(ref _workspacePage, value)) return;
            OnPropertyChanged(nameof(IsWorkspacePageOpen));
            OnPropertyChanged(nameof(IsCodePageOpen));
            OnPropertyChanged(nameof(IsOfficePageOpen));
            OnPropertyChanged(nameof(IsFilesChromeVisible));
            RaiseDualPaneState();
        }
    }

    public bool IsWorkspacePageOpen => WorkspacePage != WorkspacePageKind.Files;
    public bool IsCodePageOpen => WorkspacePage == WorkspacePageKind.Code;
    public bool IsOfficePageOpen => WorkspacePage == WorkspacePageKind.Office;

    /// <summary>
    /// File-manager chrome (tab strip, navigation toolbar, command bar, status bar) is hidden in
    /// document mode so the document owns the window. The sidebar is deliberately not part of
    /// this: it stays as the file browser next to the document.
    /// </summary>
    public bool IsFilesChromeVisible => !IsOfficePageOpen;

    public ExtensionDiscoveryService ExtensionDiscovery => _extensionDiscovery ??= new ExtensionDiscoveryService();
    public CodeEditorViewModel CodeEditor => _codeEditor ??= new CodeEditorViewModel(ExtensionDiscovery) { Shell = this };
    public ExtensionsViewModel Extensions => _extensions ??= new ExtensionsViewModel(ExtensionDiscovery, CodeEditor) { Shell = this };
    public OfficeViewModel Office => _office ??= new OfficeViewModel { Shell = this };

    /// <summary>Opens a source file in the Monaco editor page.</summary>
    public async Task OpenCodeFileAsync(string path)
    {
        await CodeEditor.OpenAsync(path);
        WorkspacePage = WorkspacePageKind.Code;
    }

    /// <summary>
    /// Opens a Word / Excel / PowerPoint file in document mode: the file-manager chrome collapses
    /// and the document takes the window, with the sidebar left as a file browser.
    /// </summary>
    public async Task OpenOfficeFileAsync(string path)
    {
        // The sidebar stays usable in document mode, so switching documents is one click away.
        // Ask before silently dropping unsaved edits.
        if (Office.HasDocument && Office.IsDirty)
        {
            var discard = await CreateUiServices().ConfirmAsync(
                Loc.T("OfficeUnsavedTitle"), Loc.T("OfficeUnsavedSwitch")).ConfigureAwait(true);
            if (!discard) return;
        }

        await Office.OpenAsync(path);

        // A failed load leaves no document behind; report it instead of collapsing the file
        // manager only to show an empty page.
        if (!Office.HasDocument)
        {
            await CreateUiServices().ShowInfoAsync(
                Loc.T("OfficeOpenFailedTitle"), Office.Status).ConfigureAwait(true);
            return;
        }

        WorkspacePage = WorkspacePageKind.Office;
    }

    /// <summary>
    /// Unified file-open dispatch: Office documents go to the built-in ribbon page, source files
    /// to Monaco, everything else to the OS default application.
    /// </summary>
    public async Task OpenFileAsync(string path)
    {
        // Entries inside an archive or on a server have no local file handle: materialize a
        // temporary copy so the Office page, Monaco and the OS handler all work unchanged.
        var localPath = await MaterializeAsync(path).ConfigureAwait(true);

        if (FileHub.Office.Services.OfficeDocumentService.IsOfficeFile(localPath))
        {
            await OpenOfficeFileAsync(localPath);
            return;
        }
        if (FileHub.Code.Services.CodeLanguageService.IsCodeFile(localPath))
        {
            await OpenCodeFileAsync(localPath);
            return;
        }
        await Task.Run(() => Process.Start(new ProcessStartInfo(localPath) { UseShellExecute = true }));
    }

    /// <summary>
    /// Returns a local path for a virtual entry, copying it to the temp folder on first use.
    /// Local paths are returned untouched.
    /// </summary>
    public static async Task<string> MaterializeAsync(string path)
    {
        if (!VfsPath.IsVirtualPath(path)) return path;

        // A web file already has a stable cache entry; reusing it avoids downloading twice.
        var scheme = VfsPath.Parse(path).Scheme;
        if (scheme is VfsScheme.Http or VfsScheme.Https)
            return await WebCacheService.DownloadAsync(path).ConfigureAwait(false);

        var name = VfsPath.Parse(path).FileName;
        if (string.IsNullOrWhiteSpace(name)) name = "entry";
        var folder = Path.Combine(Path.GetTempPath(), "FileHub", "open");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, name);

        // Overwrite so edits in a viewer do not accumulate "file (1).docx" copies.
        await VfsFileSystem.Default.CopyAsync([path], folder, VfsConflictPolicy.Overwrite)
            .ConfigureAwait(false);
        return target;
    }

    public void CloseWorkspacePage()
    {
        // Leaving document mode with unsaved edits is the second place data could vanish.
        if (IsOfficePageOpen && Office.HasDocument && Office.IsDirty)
        {
            _ = ConfirmCloseDocumentAsync();
            return;
        }
        WorkspacePage = WorkspacePageKind.Files;
    }

    private async Task ConfirmCloseDocumentAsync()
    {
        var discard = await CreateUiServices().ConfirmAsync(
            Loc.T("OfficeUnsavedTitle"), Loc.T("OfficeUnsavedClose")).ConfigureAwait(true);
        if (discard) WorkspacePage = WorkspacePageKind.Files;
    }

    /// <summary>
    /// Opens or closes the second folder pane. The second pane owns an independent path,
    /// selection and history, so a drag between the two is an ordinary VFS transfer.
    /// </summary>
    public void ToggleDualPane()
    {
        if (!IsDualPaneOpen)
        {
            if (SecondPane is null)
            {
                var pane = new MainViewModel(CreateUiServices()) { Shell = this };
                pane.RaiseDetailsPaneChanged();
                SecondPane = pane;
                _ = pane.InitializeAsync();
            }
            IsDualPaneOpen = true;
            return;
        }
        IsDualPaneOpen = false;
    }

    /// <summary>Space toggles quick look for the focused entry; Space again closes it.</summary>
    public async Task ToggleQuickLookAsync()
    {
        if (QuickLook.IsOpen)
        {
            QuickLook.Close();
            return;
        }
        var tab = ActiveTab;
        var entry = tab.PrimarySelection ?? tab.Entries.FirstOrDefault();
        if (entry is null) return;
        await QuickLook.ShowAsync(tab.Entries.ToList(), entry).ConfigureAwait(true);
    }

    public async Task QuickLookStepAsync(int delta)
    {
        if (!QuickLook.IsOpen) return;
        if (delta > 0) await QuickLook.NextAsync().ConfigureAwait(true);
        else await QuickLook.PreviousAsync().ConfigureAwait(true);
        SyncQuickLookSelection();
    }

    /// <summary>Keeps the list selection in step with the quick look page.</summary>
    private void SyncQuickLookSelection()
    {
        if (!QuickLook.IsOpen || string.IsNullOrEmpty(QuickLook.PathText)) return;
        var match = ActiveTab.Entries.FirstOrDefault(x =>
            string.Equals(x.FullPath, QuickLook.PathText, StringComparison.OrdinalIgnoreCase));
        if (match is not null) ActiveTab.SetSelection([match]);
    }

    private async Task StepUndoAsync(bool undo)
    {
        try
        {
            var operation = undo
                ? await Undo.UndoAsync().ConfigureAwait(true)
                : await Undo.RedoAsync().ConfigureAwait(true);
            if (operation is null) return;
            OnPropertyChanged(nameof(UndoLabel));
            foreach (var tab in Tabs) await tab.RefreshAsync().ConfigureAwait(true);
            if (SecondPane is not null) await SecondPane.RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            ActiveTab.ShowInfo(Loc.T("MsgOperationFailed"), ex.Message, FluentAvalonia.UI.Controls.FAInfoBarSeverity.Error);
        }
    }

    public string UndoLabel => Undo.CanUndo ? $"Undo {Undo.UndoTitle}" : "Nothing to undo";
    public string RedoLabel => Undo.CanRedo ? $"Redo {Undo.RedoTitle}" : "Nothing to redo";

    private Task<VfsConflictAnswer> ResolveConflictAsync(string targetPath)
    {
        var window = CurrentWindow();
        if (window is null) return Task.FromResult(VfsConflictAnswer.KeepBoth);
        return Dispatcher.UIThread.InvokeAsync(() => TransferConflictDialog.AskAsync(window, targetPath));
    }

    /// <summary>Every palette entry. Keywords are space separated so "hide files" finds the toggle.</summary>
    private IReadOnlyList<PaletteItem> BuildPaletteItems()
    {
        var tab = ActiveTab;
        var items = new List<PaletteItem>
        {
            new PaletteItem("New tab", "Tabs", "tab add new", () => { AddTab(); return Task.CompletedTask; }),
            new PaletteItem("Close tab", "Tabs", "tab close remove", () => { CloseActiveTab(); return Task.CompletedTask; }),
            new PaletteItem("Duplicate tab", "Tabs", "tab copy duplicate", () => { DuplicateActiveTab(); return Task.CompletedTask; }),
            new PaletteItem("Reopen closed tab", "Tabs", "tab restore undo close", () => { ReopenClosedTab(); return Task.CompletedTask; }),
            new PaletteItem("Go to home folder", "Navigate", "home folder navigate", () => tab.NavigateAsync(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))),
            new PaletteItem("Go to downloads", "Navigate", "downloads folder navigate", () => tab.NavigateAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"))),
            new PaletteItem("Go to documents", "Navigate", "documents folder navigate", () => tab.NavigateAsync(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))),
            new PaletteItem("Go up one level", "Navigate", "up parent folder", () => { tab.GoUpCommand.Execute(null); return Task.CompletedTask; }),
            new PaletteItem("Refresh", "View", "reload refresh f5", () => { tab.RefreshCommand.Execute(null); return Task.CompletedTask; }),
            new PaletteItem("Toggle details / grid view", "View", "layout icons grid list", () => { tab.ToggleViewCommand.Execute(null); return Task.CompletedTask; }),
            new PaletteItem("Toggle hidden files", "View", "hidden dotfiles show", () => { tab.ToggleHiddenCommand.Execute(null); return Task.CompletedTask; }),
            new PaletteItem("Toggle sidebar", "View", "sidebar panel left", () => { ToggleSidebarCommand.Execute(null); return Task.CompletedTask; }),
            new PaletteItem("Toggle details pane", "View", "preview pane right inspector", () => { ToggleDetailsPaneCommand.Execute(null); return Task.CompletedTask; }),
            new PaletteItem("Toggle dual pane", "View", "split two panes side by side compare", () => { ToggleDualPane(); return Task.CompletedTask; }),
            new PaletteItem("Toggle transfer panel", "View", "copy queue progress transfers", () => { IsTransferPaneOpen = !IsTransferPaneOpen; return Task.CompletedTask; }),
            new PaletteItem("Toggle theme", "View", "dark light system appearance", () => { ToggleThemeCommand.Execute(null); return Task.CompletedTask; }),
            new PaletteItem("New folder", "File", "create directory mkdir", () => { tab.NewFolderCommand.Execute(null); return Task.CompletedTask; }),
            new PaletteItem("Rename", "File", "rename f2 name", () => { tab.RenameCommand.Execute(null); return Task.CompletedTask; }),
            new PaletteItem("Batch rename", "File", "batch bulk rename pattern regex", () => tab.OpenBatchRenameAsync()),
            new PaletteItem("Delete to recycle bin", "File", "delete trash remove", () => { tab.DeleteCommand.Execute(null); return Task.CompletedTask; }),
            new PaletteItem("Copy path", "File", "clipboard path copy", () => { tab.CopyPathCommand.Execute(null); return Task.CompletedTask; }),
            new PaletteItem("Reveal in system file manager", "File", "finder explorer reveal", () => { tab.RevealCommand.Execute(null); return Task.CompletedTask; }),
            new PaletteItem("Compress selection", "Archive", "zip compress archive create", () => tab.CompressSelectionAsync()),
            new PaletteItem("Extract archive here", "Archive", "unzip extract here archive", () => tab.ExtractArchiveAsync()),
            new PaletteItem("Add network location", "Network", "remote mount smb sftp webdav s3 server", () => tab.AddRemoteLocationAsync()),
            new PaletteItem("Deep search in this folder", "Search", "find recursive search grep", () => { tab.DeepSearchCommand.Execute(null); return Task.CompletedTask; }),
            new PaletteItem("Undo", "Edit", "undo revert", () => StepUndoAsync(true)),
            new PaletteItem("Redo", "Edit", "redo repeat", () => StepUndoAsync(false)),
            new PaletteItem("Open settings", "Settings", "settings preferences theme language extensions about", () => { OpenSettingsCommand.Execute(null); return Task.CompletedTask; }),
            new PaletteItem("Back to files", "Workspace", "close document back to files escape", () => { CloseWorkspacePage(); return Task.CompletedTask; })
        };

        // Plugin contributions are appended so a third-party command sits beside the built-ins.
        foreach (var command in PluginHost.Instance.Commands)
        {
            var captured = command;
            items.Add(new PaletteItem(captured.Title, captured.Category, captured.Keywords,
                () => captured.Execute(new PluginCommandContext
                {
                    CurrentPath = tab.CurrentPath,
                    SelectedPaths = tab.SelectedEntries.Select(x => x.FullPath).ToList(),
                    NavigateAsync = path => tab.NavigateAsync(path),
                    Report = (title, message) =>
                        tab.ShowInfo(title, message,
                            FluentAvalonia.UI.Controls.FAInfoBarSeverity.Informational)
                })));
        }

        return items;
    }


    /// <summary>Forwarded so the tab-strip footer can switch theme without reaching into a tab.</summary>
    public ICommand ToggleThemeCommand => ActiveTab.ToggleThemeCommand;

    public MainViewModel EnsureFirstTab()
    {
        if (Tabs.Count == 0)
        {
            // A path passed by the shell or a filehub:// link wins over the restored session.
            // A document path opens the folder it lives in and then the document itself, so
            // "filehub report.docx" lands in document mode rather than failing to list a file.
            if (Program.StartupPath is { Length: > 0 } startup)
            {
                if (File.Exists(startup))
                {
                    var folder = Path.GetDirectoryName(startup);
                    AddTab(string.IsNullOrEmpty(folder) ? null : folder);
                    _ = OpenFileAsync(startup);
                }
                else
                {
                    AddTab(startup);
                }
                return ActiveTab;
            }
            var paths = LoadSession();
            if (paths.Count > 0)
            {
                foreach (var path in paths) AddTab(path);
            }
            else AddTab();
        }
        return ActiveTab;
    }

    /// <summary>Creates a tab. When <paramref name="path"/> is supplied the tab opens it on init.</summary>
    public void AddTab(string? path = null)
    {
        var tab = new MainViewModel(CreateUiServices()) { Shell = this, InitialPath = path ?? string.Empty };
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

    /// <summary>Activates a tab by its 1-based strip position (Files: Ctrl+1..8).</summary>
    public void ActivateByIndex(int index)
    {
        if (index >= 1 && index <= Tabs.Count) Activate(Tabs[index - 1]);
    }

    public void ActivateNextTab()
    {
        if (Tabs.Count < 2) return;
        var index = Tabs.IndexOf(ActiveTab);
        Activate(Tabs[(index + 1) % Tabs.Count]);
    }

    public void ActivatePreviousTab()
    {
        if (Tabs.Count < 2) return;
        var index = Tabs.IndexOf(ActiveTab);
        Activate(Tabs[(index - 1 + Tabs.Count) % Tabs.Count]);
    }

    /// <summary>Moves a tab within the strip; the ObservableCollection event updates the view.</summary>
    public void MoveTab(MainViewModel tab, int newIndex)
    {
        var oldIndex = Tabs.IndexOf(tab);
        if (oldIndex < 0 || newIndex < 0 || newIndex >= Tabs.Count || oldIndex == newIndex) return;
        Tabs.Move(oldIndex, newIndex);
    }

    /// <summary>Moves the active tab left/right by one slot.</summary>
    public void MoveActiveTab(int delta) => MoveTab(ActiveTab, Tabs.IndexOf(ActiveTab) + delta);

    public void CloseTab(MainViewModel tab)
    {
        if (Tabs.Count <= 1 || !Tabs.Contains(tab)) return;
        var index = Tabs.IndexOf(tab);
        RememberClosed(tab);
        Tabs.Remove(tab);
        Activate(Tabs[Math.Min(index, Tabs.Count - 1)]);
    }

    public void CloseActiveTab() => CloseTab(ActiveTab);

    public void CloseOtherTabs(MainViewModel keep)
    {
        if (!Tabs.Contains(keep)) return;
        foreach (var tab in Tabs.Where(x => !ReferenceEquals(x, keep)).ToArray())
        {
            RememberClosed(tab);
            Tabs.Remove(tab);
        }
        Activate(keep);
    }

    public void CloseTabsToRight(MainViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0) return;
        foreach (var other in Tabs.Skip(index + 1).ToArray())
        {
            RememberClosed(other);
            Tabs.Remove(other);
        }
        Activate(tab);
    }

    /// <summary>Opens the last closed folder in a fresh tab (Files: Ctrl+Shift+T).</summary>
    public void ReopenClosedTab()
    {
        if (_closedTabPaths.Count == 0) return;
        var path = _closedTabPaths[0];
        _closedTabPaths.RemoveAt(0);
        AddTab(path);
    }

    public void DuplicateActiveTab() => AddTab(ActiveTab.CurrentPath);

    private void RememberClosed(MainViewModel tab)
    {
        var path = tab.CurrentPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        _closedTabPaths.Remove(path);
        _closedTabPaths.Insert(0, path);
        if (_closedTabPaths.Count > MaxClosedTabs) _closedTabPaths.RemoveAt(_closedTabPaths.Count - 1);
    }

    public async Task InitializeAsync()
    {
        foreach (var tab in Tabs.Where(x => !x.IsReady).ToArray())
            await tab.InitializeAsync();
    }

    /// <summary>Persists open folders so the next launch restores the same tabs.</summary>
    public void SaveSession()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileHub");
            Directory.CreateDirectory(dir);
            var paths = Tabs.Where(x => !string.IsNullOrWhiteSpace(x.CurrentPath))
                .Select(x => x.CurrentPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            File.WriteAllText(Path.Combine(dir, "tabs.json"), JsonSerializer.Serialize(paths));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only profile must not prevent the app from closing.
        }
    }

    private static List<string> LoadSession()
    {
        try
        {
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FileHub", "tabs.json");
            if (!File.Exists(file)) return [];
            var paths = JsonSerializer.Deserialize<string[]>(File.ReadAllText(file)) ?? [];
            return paths.Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// UiServices resolves the current window lazily instead of capturing one at construction
    /// time, because RestartMainWindow() replaces the window on a language switch.
    /// </summary>
    private static UiServices CreateUiServices() => new()
    {
        // Route through the shell's dispatcher so Office documents and source files open in
        // built-in pages no matter which entry point was used (double-click, Enter or context menu).
        OpenFileAsync = path => Current.OpenFileAsync(path),
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
