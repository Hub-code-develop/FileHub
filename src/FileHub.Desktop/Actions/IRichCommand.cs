// Copyright (c) Files Community. Ported to FileHub under the MIT License.
// See THIRD-PARTY-NOTICES.md.
//
// Mirrors Files App's IAction / IRichCommand: one object carries the label, glyph, hotkey,
// category and executable state that every surface (toolbar, menu, shortcut) needs.

using System.Windows.Input;
using FluentAvalonia.UI.Controls;

namespace FileHub.Desktop.Actions;

public interface IRichCommand : ICommand
{
    CommandCodes Code { get; }

    string Label { get; }

    string Description { get; }

    FASymbol Symbol { get; }

    string HotKey { get; }

    ActionCategory Category { get; }

    bool IsExecutable { get; }

    /// <summary>Command parameter used by the toolbar button (e.g. the sort column).</summary>
    object? Parameter { get; }
}
