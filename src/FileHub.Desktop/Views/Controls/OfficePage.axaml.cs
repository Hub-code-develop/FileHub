using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using FileHub.Desktop.Services;
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
        _vm.PromptAsync = PromptAsync;
        _vm.ShowInfoAsync = ShowInfoAsync;
        _vm.PickImagePathAsync = PickImagePathAsync;
        _vm.PickSavePathAsync = PickSavePathAsync;
        _vm.SetConfirmHandler(ConfirmAsync);
    }

    private static Window? CurrentWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    private static async Task<string?> PromptAsync(string title, string message, string initial, string primary)
        => CurrentWindow is { } window
            ? await new DialogService(window).PromptAsync(title, message, initial, primary)
            : null;

    private static async Task ShowInfoAsync(string title, string message)
    {
        if (CurrentWindow is { } window)
            await new DialogService(window).ShowInfoAsync(title, message);
    }

    private static async Task<bool> ConfirmAsync(string title, string message)
        => CurrentWindow is { } window && await new DialogService(window).ConfirmAsync(title, message);

    private async Task<string?> PickImagePathAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("OfficeInsertImage"),
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Images")
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp" }
                }
            }
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> PickSavePathAsync(string suggestedName, string extension)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T("OfficeSaveAs"),
            SuggestedFileName = suggestedName,
            DefaultExtension = extension.TrimStart('.')
        });
        return file?.TryGetLocalPath();
    }

    private void OnParagraphGotFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not WordParagraph paragraph) return;
        paragraph.IsEditing = true;
        _vm.SelectedParagraph = paragraph;
        _vm.BeginEdit();
        _vm.RefreshSelectionState();
    }

    private void OnParagraphLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox box && box.DataContext is WordParagraph paragraph)
            paragraph.IsEditing = false;
        _vm.MarkDirty();
    }

    private void OnParagraphDisplayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not WordParagraph paragraph) return;
        paragraph.IsEditing = true;
        var textBox = (control.Parent as Panel)?.Children.OfType<TextBox>().FirstOrDefault();
        textBox?.Focus();
        e.Handled = true;
    }

    private void OnRibbonItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton button && button.DataContext is RibbonItem item && !item.IsToggle)
            button.IsChecked = false;
    }

    private void OnTableGotFocus(object? sender, RoutedEventArgs e) => _vm.BeginEdit();

    private void OnTableLostFocus(object? sender, RoutedEventArgs e) => _vm.MarkDirty();

    private void OnCellGotFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox box && box.DataContext is ExcelCell cell)
        {
            _vm.SelectCell(cell);
            _vm.BeginEdit();
        }
    }

    private void OnCellLostFocus(object? sender, RoutedEventArgs e) => _vm.MarkDirty();

    private void OnShapeGotFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox box && box.DataContext is PowerPointShape shape)
        {
            _vm.SelectedShape = shape;
            _vm.BeginEdit();
            _vm.RefreshSelectionState();
        }
    }

    private void OnShapeLostFocus(object? sender, RoutedEventArgs e) => _vm.MarkDirty();

    private void OnFormulaKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _vm.SetFormulaCommand.Execute(null);
    }

    private void OnNewNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _vm.CreateNewCommand.Execute(null);
    }
}
