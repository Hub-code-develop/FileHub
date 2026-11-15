using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;

namespace Cobalt.Desktop.Services;

/// <summary>统一用 FluentUI-CodeHub 的 FAContentDialog 做输入 / 确认 / 信息弹窗。</summary>
public sealed class DialogService
{
    private readonly Window _window;

    public DialogService(Window window) => _window = window;

    public async Task<string?> PromptAsync(string title, string message, string initial, string primary)
    {
        var box = new TextBox
        {
            Text = initial,
            PlaceholderText = "输入名称",
            MinWidth = 340,
            Margin = new Avalonia.Thickness(0, 4, 0, 0)
        };
        var panel = new StackPanel { Spacing = 8, MinWidth = 340 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);

        var dialog = new FAContentDialog
        {
            Title = title,
            Content = panel,
            PrimaryButtonText = primary,
            CloseButtonText = "取消",
            DefaultButton = FAContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync(_window);
        return result == FAContentDialogResult.Primary ? box.Text : null;
    }

    public async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new FAContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MinWidth = 340 },
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = FAContentDialogButton.Close
        };
        return await dialog.ShowAsync(_window) == FAContentDialogResult.Primary;
    }

    public async Task ShowInfoAsync(string title, string message)
    {
        var dialog = new FAContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MinWidth = 340 },
            CloseButtonText = "关闭",
            DefaultButton = FAContentDialogButton.Close
        };
        await dialog.ShowAsync(_window);
    }

    public async Task CopyTextAsync(string text)
    {
        var clipboard = _window.Clipboard;
        if (clipboard is not null) await clipboard.SetTextAsync(text);
    }
}
