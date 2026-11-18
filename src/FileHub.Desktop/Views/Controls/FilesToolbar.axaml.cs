using Avalonia.Controls;
using FileHub.Desktop.Actions;
using FluentAvalonia.UI.Controls;
using WinUI3.Fluent.Controls;

namespace FileHub.Desktop.Views.Controls;

/// <summary>
/// The Files App inner toolbar. Just like Files' Toolbar.xaml.cs, the buttons are created
/// from the command registry rather than declared one by one in XAML: the left bar gets
/// the context commands and the right bar gets the view commands with collapsed labels.
/// </summary>
public partial class FilesToolbar : UserControl
{
    private CommandManager? _commands;

    public FilesToolbar() => InitializeComponent();

    public CommandManager Commands
    {
        get => _commands!;
        set
        {
            _commands = value;
            Populate();
        }
    }

    private void Populate()
    {
        if (_commands is null) return;

        _primaryBar.Children.Clear();
        _secondaryBar.Children.Clear();

        foreach (var command in _commands.PrimaryCommands)
            _primaryBar.Children.Add(MakeButton(command, WinCommandBarLabelPosition.Right));

        foreach (var command in _commands.SecondaryCommands)
            _secondaryBar.Children.Add(MakeButton(command, WinCommandBarLabelPosition.Collapsed));
    }

    private static WinAppBarButton MakeButton(IRichCommand command, WinCommandBarLabelPosition position)
    {
        var button = new WinAppBarButton
        {
            Label = command.Label,
            LabelPosition = position,
            Command = command,
            CommandParameter = command.Parameter,
            Icon = new FASymbolIcon { Symbol = command.Symbol, FontSize = 16 }
        };
        ToolTip.SetTip(button, command.Description);
        return button;
    }
}
