// Copyright (c) Files Community. Ported to FileHub under the MIT License.
// See THIRD-PARTY-NOTICES.md.
//
// Equivalent of Files App's BaseUIAction + RichCommand: metadata + execution are kept in
// one object so the toolbar and the context menu cannot drift apart.

using System;
using System.Windows.Input;
using FileHub.Desktop.Services;
using FileHub.Desktop.ViewModels;
using FluentAvalonia.UI.Controls;

namespace FileHub.Desktop.Actions;

public sealed class RichCommand : ViewModelBase, IRichCommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private readonly string _labelKey;
    private readonly string _descriptionKey;

    public RichCommand(
        CommandCodes code,
        string labelKey,
        string descriptionKey,
        FASymbol symbol,
        string hotKey,
        ActionCategory category,
        Action<object?> execute,
        Func<object?, bool>? canExecute = null,
        object? parameter = null)
    {
        Code = code;
        _labelKey = labelKey;
        _descriptionKey = descriptionKey;
        Symbol = symbol;
        HotKey = hotKey;
        Category = category;
        _execute = execute;
        _canExecute = canExecute;
        Parameter = parameter;
    }

    public CommandCodes Code { get; }
    public FASymbol Symbol { get; }
    public string HotKey { get; }
    public ActionCategory Category { get; }
    public object? Parameter { get; }

    // Localized lazily: Loc.SetLanguage replaces the window, so a newly built UI reads
    // the current language without any command having to cache a stale string.
    public string Label => Loc.T(_labelKey);
    public string Description => Loc.T(_descriptionKey);
    public bool IsExecutable => _canExecute?.Invoke(Parameter) ?? true;

    public bool CanExecute(object? parameter)
        => IsExecutable && (_canExecute?.Invoke(parameter ?? Parameter) ?? true);

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter)) _execute(parameter ?? Parameter);
    }

    public event EventHandler? CanExecuteChanged;

    public void RaiseCanExecuteChanged()
        => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
