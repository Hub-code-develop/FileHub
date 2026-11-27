using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using FileHub.Desktop.ViewModels;
using FileHub.Office.Models;

namespace FileHub.Desktop.Views.Controls;

public partial class OfficePage : UserControl
{
    private readonly OfficeViewModel _vm;

    public OfficePage()
    {
        InitializeComponent();
        _vm = ShellViewModel.Current.Office;
        DataContext = _vm;
    }

    private void OnParagraphGotFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox box && box.DataContext is WordParagraph p)
            _vm.SelectedParagraph = p;
    }

    private void OnParagraphLostFocus(object? sender, RoutedEventArgs e)
    {
        _vm.MarkDirty();
    }

    private void OnCellGotFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox box && box.DataContext is ExcelCell c)
            _vm.SelectedCell = c;
    }

    private void OnCellLostFocus(object? sender, RoutedEventArgs e)
    {
        _vm.MarkDirty();
    }

    private void OnNewNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _vm.CreateNewCommand.Execute(null);
        }
    }
}
