// Copyright (c) 2026 Hub-code-develop. Licensed under the MIT License.
//
// Ctrl+Shift+P command palette. Every verb the window can perform shows up here with a keyword
// and a category, so the user never has to hunt through menus — the same idea as VS Code's
// command palette, scoped to file-management actions.

using System.Collections.ObjectModel;

namespace FileHub.Desktop.ViewModels;

public sealed class PaletteItem
{
    public PaletteItem(string title, string category, string keywords, Func<Task>? invoke)
    {
        Title = title;
        Category = category;
        Keywords = keywords;
        Invoke = invoke;
    }

    public string Title { get; }
    public string Category { get; }
    public string Keywords { get; }
    public Func<Task>? Invoke { get; }

    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        foreach (var term in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var hit = Title.Contains(term, StringComparison.OrdinalIgnoreCase)
                      || Keywords.Contains(term, StringComparison.OrdinalIgnoreCase)
                      || Category.Contains(term, StringComparison.OrdinalIgnoreCase);
            if (!hit) return false;
        }
        return true;
    }
}

public sealed class CommandPaletteViewModel : ViewModelBase
{
    private readonly Func<IReadOnlyList<PaletteItem>> _source;
    private bool _isOpen;
    private string _query = string.Empty;
    private PaletteItem? _selectedItem;
    private int _selectedIndex;

    public CommandPaletteViewModel(Func<IReadOnlyList<PaletteItem>> source) => _source = source;

    public ObservableCollection<PaletteItem> Results { get; } = [];

    public bool IsOpen
    {
        get => _isOpen;
        private set
        {
            if (Set(ref _isOpen, value)) OnPropertyChanged(nameof(IsEmpty));
        }
    }

    public bool IsEmpty => IsOpen && Results.Count == 0;
    public int ResultCount => Results.Count;

    public string Query
    {
        get => _query;
        set
        {
            if (!Set(ref _query, value)) return;
            Refresh();
        }
    }

    public PaletteItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!Set(ref _selectedItem, value)) return;
            if (value is not null) _selectedIndex = Results.IndexOf(value);
        }
    }

    public void Open()
    {
        Query = string.Empty;
        Refresh();
        IsOpen = true;
    }

    public void Close()
    {
        IsOpen = false;
        Query = string.Empty;
    }

    public void MoveSelection(int delta)
    {
        if (Results.Count == 0) return;
        _selectedIndex = Math.Clamp(_selectedIndex + delta, 0, Results.Count - 1);
        SelectedItem = Results[_selectedIndex];
    }

    public async Task ExecuteSelectedAsync()
    {
        var item = SelectedItem ?? Results.FirstOrDefault();
        if (item is null) return;
        Close();
        if (item.Invoke is not null) await item.Invoke().ConfigureAwait(true);
    }

    private void Refresh()
    {
        var items = _source()
            .Where(x => x.Matches(_query))
            .OrderBy(x => x.Title.StartsWith(_query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .Take(60)
            .ToList();

        Results.Clear();
        foreach (var item in items) Results.Add(item);

        _selectedIndex = 0;
        SelectedItem = Results.FirstOrDefault();
        OnPropertyChanged(nameof(ResultCount));
        OnPropertyChanged(nameof(IsEmpty));
    }
}
