// Copyright (c) Files Community. Ported to FileHub under the MIT License.
// See THIRD-PARTY-NOTICES.md.
//
// Files App's CommandManager is the single registry every UI surface pulls from:
// CommandBar, context menus, keyboard shortcuts and the command palette. FileHub keeps
// that design. Commands resolve the active tab at execution time, so a tab switch never
// leaves a button pointing at the previous folder.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using FileHub.Desktop.ViewModels;
using FluentAvalonia.UI.Controls;

namespace FileHub.Desktop.Actions;

public sealed class CommandManager : ViewModelBase, IEnumerable<IRichCommand>
{
    private readonly ShellViewModel _shell;
    private readonly Dictionary<CommandCodes, IRichCommand> _commands = [];
    private readonly List<IRichCommand> _primary = [];
    private readonly List<IRichCommand> _secondary = [];
    private readonly List<IRichCommand> _context = [];

    private MainViewModel Tab => _shell.ActiveTab;

    public CommandManager(ShellViewModel shell)
    {
        _shell = shell;

        // --- Files: ContextCommandBar primary commands -------------------------------
        Add(CommandCodes.NewFolder, "LblNew", "NewFolder", FASymbol.Add, "Ctrl+Shift+N",
            ActionCategory.FileSystem, p => Tab.NewFolderCommand.Execute(p), primary: true);
        Add(CommandCodes.Cut, "LblCut", "Cut", FASymbol.Cut, "Ctrl+X",
            ActionCategory.FileSystem, p => Tab.CutCommand.Execute(p), primary: true, context: true);
        Add(CommandCodes.Copy, "LblCopy", "Copy", FASymbol.Copy, "Ctrl+C",
            ActionCategory.FileSystem, p => Tab.CopyCommand.Execute(p), primary: true, context: true);
        Add(CommandCodes.Paste, "LblPaste", "Paste", FASymbol.Paste, "Ctrl+V",
            ActionCategory.FileSystem, p => Tab.PasteCommand.Execute(p), primary: true, context: true);
        Add(CommandCodes.Rename, "LblRename", "Rename", FASymbol.Edit, "F2",
            ActionCategory.FileSystem, p => Tab.RenameCommand.Execute(p), primary: true, context: true);
        Add(CommandCodes.Delete, "LblDelete", "Delete", FASymbol.Delete, "Delete",
            ActionCategory.FileSystem, p => Tab.DeleteCommand.Execute(p), primary: true, context: true);

        // --- Files: BaseCommandBar (view commands, label collapsed) ------------------
        Add(CommandCodes.ToggleHidden, "LblHidden", "ToggleHidden", FASymbol.View, "Ctrl+H",
            ActionCategory.Display, p => Tab.ToggleHiddenCommand.Execute(p), secondary: true);
        Add(CommandCodes.SetSortByName, "LblSort", "SortName", FASymbol.Sort, string.Empty,
            ActionCategory.Display, p => Tab.SetSortCommand.Execute("Name"), secondary: true,
            parameter: "Name");
        Add(CommandCodes.ToggleView, "LblLayout", "ToggleView", FASymbol.ViewAll, "Ctrl+Shift+2",
            ActionCategory.Display, p => Tab.ToggleViewCommand.Execute(p), secondary: true);
        Add(CommandCodes.ToggleDetailsPane, "LblDetails", "ToggleDetailsPane", FASymbol.ContactInfo,
            "Ctrl+Shift+I", ActionCategory.Display, p => _shell.ToggleDetailsPaneCommand.Execute(p),
            secondary: true);

        // --- Context-only commands ---------------------------------------------------
        Add(CommandCodes.Open, "CtxOpen", "CtxOpen", FASymbol.OpenFile, "Enter",
            ActionCategory.FileSystem, p => Tab.OpenCommand.Execute(p), context: true);
        Add(CommandCodes.OpenSelected, "LblOpen", "CtxOpen", FASymbol.OpenFile, "Enter",
            ActionCategory.FileSystem, p => Tab.OpenSelectedCommand.Execute(p));
        Add(CommandCodes.Reveal, "CtxReveal", "CtxReveal", FASymbol.OpenFolder, string.Empty,
            ActionCategory.FileSystem, p => Tab.RevealCommand.Execute(p), context: true);
        Add(CommandCodes.CopyPath, "CtxCopyPath", "CtxCopyPath", FASymbol.Link, "Ctrl+Shift+C",
            ActionCategory.FileSystem, p => Tab.CopyPathCommand.Execute(p), context: true);
        Add(CommandCodes.SelectAll, "LblSelect", "CtxSelectAll", FASymbol.Accept, "Ctrl+A",
            ActionCategory.FileSystem, p => Tab.SelectAllCommand.Execute(p), context: true);
        Add(CommandCodes.Properties, "LblProperties", "CtxProperties", FASymbol.ContactInfo,
            "Alt+Enter", ActionCategory.FileSystem, p => Tab.PropertiesCommand.Execute(p),
            context: true);

        // --- Navigation / global -----------------------------------------------------
        Add(CommandCodes.GoBack, "Back", "Back", FASymbol.Back, "Alt+Left",
            ActionCategory.Navigation, p => Tab.GoBackCommand.Execute(p));
        Add(CommandCodes.GoForward, "Forward", "Forward", FASymbol.ChevronRight, "Alt+Right",
            ActionCategory.Navigation, p => Tab.GoForwardCommand.Execute(p));
        Add(CommandCodes.GoUp, "Up", "Up", FASymbol.Up, "Alt+Up",
            ActionCategory.Navigation, p => Tab.GoUpCommand.Execute(p));
        Add(CommandCodes.Refresh, "Refresh", "Refresh", FASymbol.Refresh, "F5",
            ActionCategory.Navigation, p => Tab.RefreshCommand.Execute(p));
        Add(CommandCodes.EditAddress, "EditAddress", "EditAddress", FASymbol.Edit, "Ctrl+L",
            ActionCategory.Navigation, p => Tab.EditAddressCommand.Execute(p));
        Add(CommandCodes.DeepSearch, "DeepSearch", "DeepSearch", FASymbol.Find, "Ctrl+F",
            ActionCategory.Navigation, p => Tab.DeepSearchCommand.Execute(p));
        Add(CommandCodes.NewTab, "NewTab", "NewTab", FASymbol.Add, "Ctrl+T",
            ActionCategory.Global, p => _shell.NewTabCommand.Execute(p));
        Add(CommandCodes.CloseTab, "CloseTab", "CloseTab", FASymbol.Delete, "Ctrl+W",
            ActionCategory.Global, p => _shell.CloseActiveTab());
        Add(CommandCodes.DuplicateTab, "DuplicateTab", "DuplicateTab", FASymbol.Copy, "Ctrl+Shift+D",
            ActionCategory.Global, p => _shell.DuplicateActiveTab());
        Add(CommandCodes.NextTab, "NextTab", "NextTab", FASymbol.ChevronRight, "Ctrl+Tab",
            ActionCategory.Navigation, p => _shell.ActivateNextTab());
        Add(CommandCodes.PreviousTab, "PreviousTab", "PreviousTab", FASymbol.Back, "Ctrl+Shift+Tab",
            ActionCategory.Navigation, p => _shell.ActivatePreviousTab());
        Add(CommandCodes.ReopenClosedTab, "ReopenClosedTab", "ReopenClosedTab", FASymbol.Refresh,
            "Ctrl+Shift+T", ActionCategory.Global, p => _shell.ReopenClosedTab());
        Add(CommandCodes.CloseOtherTabs, "CloseOtherTabs", "CloseOtherTabs", FASymbol.Delete,
            string.Empty, ActionCategory.Global, p => _shell.CloseOtherTabs(_shell.ActiveTab));
        Add(CommandCodes.CloseTabsToRight, "CloseTabsToRight", "CloseTabsToRight", FASymbol.Delete,
            string.Empty, ActionCategory.Global, p => _shell.CloseTabsToRight(_shell.ActiveTab));
        Add(CommandCodes.MoveTabLeft, "MoveTabLeft", "MoveTabLeft", FASymbol.Back, "Ctrl+Shift+Left",
            ActionCategory.Global, p => _shell.MoveActiveTab(-1));
        Add(CommandCodes.MoveTabRight, "MoveTabRight", "MoveTabRight", FASymbol.ChevronRight,
            "Ctrl+Shift+Right", ActionCategory.Global, p => _shell.MoveActiveTab(1));
        Add(CommandCodes.ToggleSidebar, "LblToggleSidebar", "LblToggleSidebar", FASymbol.ViewAll,
            "Ctrl+Shift+S", ActionCategory.Global, p => _shell.ToggleSidebarCommand.Execute(p));
        Add(CommandCodes.ToggleTheme, "ToggleTheme", "ToggleTheme", FASymbol.Settings, string.Empty,
            ActionCategory.Global, p => Tab.ToggleThemeCommand.Execute(p));
    }

    public IRichCommand this[CommandCodes code]
        => _commands.TryGetValue(code, out var command) ? command : None;

    /// <summary>Files' CommandManager[HotKey] lookup, used by keyboard handling.</summary>
    public IRichCommand this[string hotKey]
        => _commands.Values.FirstOrDefault(x =>
            string.Equals(x.HotKey, hotKey, StringComparison.OrdinalIgnoreCase)) ?? None;

    public IReadOnlyList<IRichCommand> PrimaryCommands => _primary;
    public IReadOnlyList<IRichCommand> SecondaryCommands => _secondary;
    public IReadOnlyList<IRichCommand> ContextCommands => _context;
    public IEnumerable<IRichCommand> AllCommands => _commands.Values;

    /// <summary>A harmless no-op so indexers never have to return null.</summary>
    public static IRichCommand None { get; } = new RichCommand(
        CommandCodes.None, "Unknown", "Unknown", FASymbol.Folder, string.Empty,
        ActionCategory.Unspecified, _ => { });

    public bool Execute(CommandCodes code, object? parameter = null)
    {
        var command = this[code];
        if (command == None || !command.CanExecute(parameter)) return false;
        command.Execute(parameter);
        return true;
    }

    public IEnumerator<IRichCommand> GetEnumerator() => _commands.Values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void Add(
        CommandCodes code,
        string labelKey,
        string descriptionKey,
        FASymbol symbol,
        string hotKey,
        ActionCategory category,
        Action<object?> execute,
        bool primary = false,
        bool secondary = false,
        bool context = false,
        object? parameter = null)
    {
        var command = new RichCommand(code, labelKey, descriptionKey, symbol, hotKey,
            category, execute, parameter: parameter);
        _commands[code] = command;
        if (primary) _primary.Add(command);
        if (secondary) _secondary.Add(command);
        if (context) _context.Add(command);
    }
}
