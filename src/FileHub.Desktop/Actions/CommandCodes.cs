// Copyright (c) Files Community. Ported to FileHub under the MIT License.
// See THIRD-PARTY-NOTICES.md.
//
// Files App identifies every command with a CommandCodes enum value so toolbars, context
// menus, the command palette and keyboard shortcuts can all reference the same action.
// FileHub keeps that shape; the command set itself is the cross-platform subset.

namespace FileHub.Desktop.Actions;

/// <summary>Where a command appears in Files App's command palette.</summary>
public enum ActionCategory
{
    Unspecified,
    FileSystem,
    Display,
    Navigation,
    Global
}

/// <summary>Stable identities for every FileHub command, mirroring Files' CommandCodes.</summary>
public enum CommandCodes
{
    None = 0,

    // File system
    NewFolder,
    Cut,
    Copy,
    Paste,
    Rename,
    Delete,
    Open,
    OpenSelected,
    Reveal,
    CopyPath,
    Properties,
    SelectAll,

    // Display
    ToggleHidden,
    SetSortByName,
    SetSortBySize,
    SetSortByKind,
    SetSortByModified,
    ToggleView,
    ToggleDetailsPane,
    ToggleTheme,

    // Navigation
    GoBack,
    GoForward,
    GoUp,
    Refresh,
    EditAddress,
    DeepSearch,
    NewTab,

    // Global
    ToggleSidebar
}
