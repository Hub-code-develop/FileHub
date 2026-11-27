// Copyright (c) 2026 Hub-code-develop. Licensed under the MIT License.
//
// Batch rename dialog: pattern in, live preview out. The preview is the whole point — the user
// must see every resulting name, including collisions, before anything touches disk.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FileHub.Files.Services;
using FileHub.Files.Vfs;

namespace FileHub.Desktop.Views;

public sealed class BatchRenameDialog : Window
{
    private readonly IReadOnlyList<string> _paths;
    private readonly TaskCompletionSource<RenamePlan?> _result = new();
    private readonly ListBox _preview = new() { Height = 240, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _status = new() { FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };

    private readonly TextBox _pattern = new() { Text = "{name}{ext}" };
    private readonly TextBox _prefix = new();
    private readonly TextBox _suffix = new();
    private readonly TextBox _regexFind = new();
    private readonly TextBox _regexReplace = new();
    private readonly NumericUpDown _start = new() { Value = 1, Minimum = 0, Maximum = 999999, Increment = 1 };
    private readonly ComboBox _case = new()
    {
        ItemsSource = new[] { "Keep", "UPPER", "lower", "Title Case" },
        SelectedIndex = 0
    };

    private CancellationTokenSource? _previewCts;

    private BatchRenameDialog(IReadOnlyList<string> paths)
    {
        _paths = paths;
        Title = $"Batch rename - {paths.Count} item(s)";
        Width = 640;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var grid = new Grid
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto")
        };

        var form = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,120"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto"),
            RowSpacing = 8,
            ColumnSpacing = 8
        };

        void Field(int row, string label, Control input, int column = 1)
        {
            var text = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12
            };
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 0);
            form.Children.Add(text);
            Grid.SetRow(input, row);
            Grid.SetColumn(input, column);
            form.Children.Add(input);
        }

        Field(0, "Pattern", _pattern);
        Field(1, "Prefix", _prefix, 1);
        Grid.SetColumnSpan(_prefix, 3);
        Field(2, "Suffix", _suffix, 1);
        Grid.SetColumnSpan(_suffix, 3);

        // Counter row: start value beside the regex row so the dialog stays compact.
        var counter = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        counter.Children.Add(new TextBlock { Text = "Start", VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
        counter.Children.Add(_start);
        counter.Children.Add(new TextBlock { Text = "Case", VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(8, 0, 0, 0) });
        counter.Children.Add(_case);
        Grid.SetRow(counter, 3);
        Grid.SetColumn(counter, 0);
        Grid.SetColumnSpan(counter, 4);
        form.Children.Add(counter);

        var regexRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*"), ColumnSpacing = 8 };
        regexRow.Children.Add(new TextBlock { Text = "Regex", VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
        Grid.SetColumn(_regexFind, 1);
        regexRow.Children.Add(_regexFind);
        var arrow = new TextBlock { Text = "to", VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        Grid.SetColumn(arrow, 2);
        regexRow.Children.Add(arrow);
        Grid.SetColumn(_regexReplace, 3);
        regexRow.Children.Add(_regexReplace);
        Grid.SetRow(regexRow, 4);
        Grid.SetColumn(regexRow, 0);
        Grid.SetColumnSpan(regexRow, 4);
        form.Children.Add(regexRow);

        Grid.SetRow(form, 0);
        grid.Children.Add(form);

        var hint = new TextBlock
        {
            Text = "Tokens: {name} {ext} {orig} {n} {n:000} {date} {time} {modified} {exif:date} {exif:model} | {name|upper} {name|lower} {name|title} {name|slug}",
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0)
        };
        Grid.SetRow(hint, 1);
        grid.Children.Add(hint);

        _preview.ItemsSource = new List<RenamePreviewItem>();
        _preview.ItemTemplate = new FuncDataTemplate<RenamePreviewItem>((item, _) =>
        {
            var broken = !item.IsValid || item.Conflict;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*"), Margin = new Thickness(2, 1) };
            var source = new TextBlock
            {
                Text = item.SourceName,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Opacity = 0.65
            };
            var arrow = new TextBlock { Text = "->", Margin = new Thickness(8, 0), FontSize = 12, Opacity = 0.5 };
            var target = new TextBlock
            {
                Text = item.TargetName,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = broken ? Brushes.OrangeRed : null
            };
            Grid.SetColumn(arrow, 1);
            Grid.SetColumn(target, 2);
            row.Children.Add(source);
            row.Children.Add(arrow);
            row.Children.Add(target);
            return row;
        });
        Grid.SetRow(_preview, 2);
        grid.Children.Add(_preview);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 10, 0, 0) };
        footer.Children.Add(_status);
        var cancel = new Button { Content = "Cancel", MinWidth = 88 };
        cancel.Click += (_, _) => { _result.TrySetResult(null); Close(); };
        Grid.SetColumn(cancel, 1);
        footer.Children.Add(cancel);
        var apply = new Button { Content = "Rename", MinWidth = 96, Classes = { "accent" } };
        apply.Click += (_, _) => { _result.TrySetResult(Current); Close(); };
        Grid.SetColumn(apply, 2);
        footer.Children.Add(apply);
        Grid.SetRow(footer, 3);
        grid.Children.Add(footer);

        Content = grid;
        Closed += (_, _) => _result.TrySetResult(null);

        foreach (var box in new[] { _pattern, _prefix, _suffix, _regexFind, _regexReplace })
            box.TextChanged += (_, _) => SchedulePreview();
        _start.ValueChanged += (_, _) => SchedulePreview();
        _case.SelectionChanged += (_, _) => SchedulePreview();

        Opened += (_, _) => _ = RefreshAsync();
    }

    private RenamePlan? Current { get; set; }

    /// <summary>Shows the dialog and returns the confirmed plan, or null when cancelled.</summary>
    public static async Task<RenamePlan?> ShowAsync(IReadOnlyList<string> paths)
    {
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow;
        var dialog = new BatchRenameDialog(paths);
        if (owner is null) return null;
        await dialog.ShowDialog(owner).ConfigureAwait(true);
        return await dialog._result.Task.ConfigureAwait(true);
    }

    private void SchedulePreview()
    {
        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;
        DispatcherTimer.RunOnce(() =>
        {
            if (!token.IsCancellationRequested) _ = RefreshAsync();
        }, TimeSpan.FromMilliseconds(160));
    }

    private async Task RefreshAsync()
    {
        try
        {
            var options = new BatchRenameOptions
            {
                Pattern = string.IsNullOrWhiteSpace(_pattern.Text) ? "{name}{ext}" : _pattern.Text!,
                Prefix = _prefix.Text ?? string.Empty,
                Suffix = _suffix.Text ?? string.Empty,
                Start = (int)(_start.Value ?? 1),
                Step = 1,
                FindPattern = string.IsNullOrWhiteSpace(_regexFind.Text) ? null : _regexFind.Text,
                Replacement = _regexReplace.Text,
                CaseMode = _case.SelectedIndex switch
                {
                    1 => RenameCaseMode.Upper,
                    2 => RenameCaseMode.Lower,
                    3 => RenameCaseMode.Title,
                    _ => RenameCaseMode.Keep
                }
            };

            var plan = await BatchRenameService.PreviewAsync(_paths, options).ConfigureAwait(true);
            Current = plan;
            _preview.ItemsSource = plan.Items;

            var conflicts = plan.Items.Count(x => x.Conflict);
            var invalid = plan.Items.Count(x => !x.IsValid);
            _status.Text = plan.Error is { Length: > 0 } error
                ? error
                : conflicts == 0 && invalid == 0
                    ? $"{plan.Items.Count} name(s) will change."
                    : $"{conflicts} conflict(s), {invalid} invalid name(s). Resolve them before renaming.";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            _status.Text = ex.Message;
        }
    }
}
