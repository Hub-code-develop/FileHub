// Copyright (c) 2026 Hub-code-develop. Licensed under the MIT License.
//
// Conflict prompt for transfers. Built in code rather than XAML because it is short, has no
// styling requirements beyond the default theme, and must be callable from a worker thread's
// callback (always marshalled to the UI thread by the caller).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FileHub.Files.Vfs;

namespace FileHub.Desktop.Views;

public sealed class TransferConflictDialog : Window
{
    /// <summary>"Apply to all" is remembered for the rest of the session, like Explorer does.</summary>
    private static VfsConflictAnswer? _remembered;

    private readonly TaskCompletionSource<VfsConflictAnswer> _result = new();

    private TransferConflictDialog(string targetPath)
    {
        Title = "Item already exists";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var applyAll = new CheckBox { Content = "Apply this choice to all remaining items", Margin = new Thickness(0, 12, 0, 0) };

        Button Make(string text, VfsConflictAnswer answer, bool primary = false)
        {
            var button = new Button
            {
                Content = text,
                MinWidth = 96,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Classes = { primary ? "accent" : string.Empty }
            };
            button.Click += (_, _) =>
            {
                if (applyAll.IsChecked == true) _remembered = answer;
                _result.TrySetResult(answer);
                Close();
            };
            return button;
        }

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "An item with this name already exists in the destination:",
                    TextWrapping = TextWrapping.Wrap
                },
                new SelectableTextBlock
                {
                    Text = targetPath,
                    FontSize = 12,
                    Opacity = 0.75,
                    TextWrapping = TextWrapping.Wrap
                },
                applyAll,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Margin = new Thickness(0, 8, 0, 0),
                    Children =
                    {
                        Make("Skip", VfsConflictAnswer.Skip),
                        Make("Keep both", VfsConflictAnswer.KeepBoth, primary: true),
                        Make("Replace", VfsConflictAnswer.Overwrite),
                        Make("Cancel", VfsConflictAnswer.Cancel)
                    }
                }
            }
        };

        Closed += (_, _) => _result.TrySetResult(VfsConflictAnswer.Cancel);
    }

    /// <summary>Asks the user what to do about an existing target. Must run on the UI thread.</summary>
    public static async Task<VfsConflictAnswer> AskAsync(Window? owner, string targetPath)
    {
        if (_remembered is { } remembered) return remembered;
        if (owner is null) return VfsConflictAnswer.KeepBoth;

        var dialog = new TransferConflictDialog(targetPath);
        await dialog.ShowDialog(owner).ConfigureAwait(true);
        return await dialog._result.Task.ConfigureAwait(true);
    }

    /// <summary>Clears the session-wide "apply to all" choice (used by tests and sign-out).</summary>
    public static void ResetRememberedChoice() => _remembered = null;
}
