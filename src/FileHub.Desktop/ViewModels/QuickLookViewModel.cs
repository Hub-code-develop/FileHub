// Copyright (c) 2026 Hub-code-develop. Licensed under the MIT License.
//
// Space-bar quick look: a full-window overlay that previews the focused entry without changing
// the folder or the selection. Mirrors macOS Quick Look: Space opens, Space or Esc closes,
// arrow keys walk the current folder.

using Avalonia.Media.Imaging;
using FileHub.Desktop.Services;
using FileHub.Files.Models;
using FileHub.Files.Services;
using FileHub.Files.Vfs;

namespace FileHub.Desktop.ViewModels;

/// <summary>A single label/value row in the quick look metadata list (XAML-friendly, no tuple).</summary>
public sealed class PreviewField
{
    public PreviewField(string label, string value) { Label = label; Value = value; }
    public string Label { get; }
    public string Value { get; }
}

public sealed class QuickLookViewModel : ViewModelBase
{
    private readonly List<FileEntryViewModel> _items = [];
    private int _index = -1;
    private CancellationTokenSource? _loadCts;

    private bool _isOpen;
    private bool _isLoading;
    private string _title = string.Empty;
    private string _subtitle = string.Empty;
    private string _pathText = string.Empty;
    private string _counterText = string.Empty;
    private string? _notice;
    private Bitmap? _image;
    private string? _text;
    private IReadOnlyList<PreviewField> _fields = [];

    public bool IsOpen { get => _isOpen; private set => Set(ref _isOpen, value); }
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }
    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Subtitle { get => _subtitle; private set => Set(ref _subtitle, value); }
    public string PathText { get => _pathText; private set => Set(ref _pathText, value); }
    public string CounterText { get => _counterText; private set => Set(ref _counterText, value); }
    public string? Notice { get => _notice; private set { if (Set(ref _notice, value)) OnPropertyChanged(nameof(HasNotice)); } }
    public bool HasNotice => !string.IsNullOrEmpty(_notice);

    public Bitmap? Image
    {
        get => _image;
        private set
        {
            if (Set(ref _image, value)) OnPropertyChanged(nameof(HasImage));
        }
    }

    public bool HasImage => _image is not null;

    public string? Text
    {
        get => _text;
        private set
        {
            if (Set(ref _text, value)) OnPropertyChanged(nameof(HasText));
        }
    }

    public bool HasText => !string.IsNullOrEmpty(_text);

    public IReadOnlyList<PreviewField> Fields
    {
        get => _fields;
        private set
        {
            if (Set(ref _fields, value)) OnPropertyChanged(nameof(HasFields));
        }
    }

    public bool HasFields => _fields.Count > 0;

    /// <summary>True when neither an image nor text is available and the icon fallback is used.</summary>
    public bool IsIconOnly => !HasImage && !HasText;

    public async Task ShowAsync(IReadOnlyList<FileEntryViewModel> items, FileEntryViewModel entry)
    {
        _items.Clear();
        _items.AddRange(items);
        _index = Math.Max(0, _items.IndexOf(entry));
        IsOpen = true;
        await LoadCurrentAsync().ConfigureAwait(true);
    }

    public Task NextAsync() => StepAsync(1);
    public Task PreviousAsync() => StepAsync(-1);

    private async Task StepAsync(int delta)
    {
        if (_items.Count == 0) return;
        _index = (_index + delta + _items.Count) % _items.Count;
        await LoadCurrentAsync().ConfigureAwait(true);
    }

    public void Close()
    {
        _loadCts?.Cancel();
        IsOpen = false;
        // Drop the decoded bitmap so a 40 MB photo is not kept alive while the overlay is closed.
        Image = null;
        Text = null;
        Fields = [];
        Notice = null;
    }

    private async Task LoadCurrentAsync()
    {
        if (_index < 0 || _index >= _items.Count) return;
        var entry = _items[_index];
        var model = entry.Model;

        Title = model.Name;
        Subtitle = $"{entry.Kind} · {model.SizeText}";
        PathText = model.FullPath;
        CounterText = _items.Count > 1 ? $"{_index + 1} / {_items.Count}" : string.Empty;
        Image = null;
        Text = null;
        Notice = null;
        Fields = BuildBasicFields(model);

        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;

        if (entry.IsDirectory) return;

        IsLoading = true;
        try
        {
            if (entry.PreviewKind == PreviewKind.Image)
            {
                Image = await ThumbnailService.Instance.LoadAsync(model.FullPath, 1600, token);
            }
            else if (entry.PreviewKind == PreviewKind.Text)
            {
                Text = await PreviewService.ReadTextPreviewAsync(model.FullPath, 12000, token);
            }

            if (Image is null && Text is null)
            {
                var inspection = await FileInspectionService.InspectAsync(model, token).ConfigureAwait(true);
                if (token.IsCancellationRequested) return;
                Notice = inspection.Notice;
                Fields = inspection.Fields.Count > 0 ? inspection.Fields : Fields;
                Text = inspection.Text;
                if (inspection.ThumbnailBytes is { Length: > 0 } bytes)
                    Image = await ThumbnailService.Instance.LoadFromBytesAsync(bytes, 1600, token);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (!token.IsCancellationRequested) IsLoading = false;
        }
    }

    private static IReadOnlyList<PreviewField> BuildBasicFields(FileEntry model)
    {
        return
        [
            new PreviewField("Kind", model.Kind),
            new PreviewField("Size", model.IsDirectory ? "Folder" : FileSystemService.FormatSize(model.Size)),
            new PreviewField("Modified", model.ModifiedText)
        ];
    }
}
