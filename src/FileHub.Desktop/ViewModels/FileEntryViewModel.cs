using Avalonia.Media.Imaging;
using FileHub.Desktop.Services;
using FileHub.Files.Models;
using FileHub.Files.Vfs;
using FileHub.Files.Services;
using FluentAvalonia.UI.Controls;

namespace FileHub.Desktop.ViewModels;

public sealed class FileEntryViewModel : ViewModelBase
{
    private Bitmap? _thumbnail;
    private string? _textPreview;
    private bool _previewRequested;

    public FileEntryViewModel(FileEntry model)
    {
        Model = model;
        Symbol = PickSymbol(model.Category);
        PreviewKind = PreviewService.GetKind(model);
    }

    public FileEntry Model { get; }

    public string Name => Model.Name;

    /// <summary>
    /// Name as rendered in the list. "Show file extensions" is off by default only when the user
    /// turns it off; the real name is still used for rename, copy path and open.
    /// </summary>
    public string DisplayName
    {
        get
        {
            if (IsDirectory || Preferences.Current.ShowFileExtensions) return Name;
            var dot = Name.LastIndexOf('.');
            return dot > 0 ? Name[..dot] : Name;
        }
    }

    public void RaiseDisplayNameChanged() => OnPropertyChanged(nameof(DisplayName));

    public string FullPath => Model.FullPath;
    public string SizeText => Model.SizeText;
    public string Kind => Model.IsDirectory
        ? Loc.T("KindFolder")
        : string.IsNullOrWhiteSpace(Model.Extension)
            ? Loc.T("KindFile")
            : Loc.F("KindExtension", Model.Extension.TrimStart('.').ToUpperInvariant());
    public string ModifiedText => Model.ModifiedText;
    public bool IsDirectory => Model.IsDirectory;
    public string Category => Model.Category;

    /// <summary>Bound directly to FASymbolIcon.Symbol so XAML needs no converter.</summary>
    public FASymbol Symbol { get; }

    public PreviewKind PreviewKind { get; }

    /// <summary>True when a real bitmap can be shown instead of the category symbol.</summary>
    public bool HasThumbnail => PreviewKind == PreviewKind.Image && _thumbnail is not null;

    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            _thumbnail = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasThumbnail));
            OnPropertyChanged(nameof(HasVisualPreview));
        }
    }

    public string? TextPreview
    {
        get => _textPreview;
        private set
        {
            _textPreview = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasTextPreview));
            OnPropertyChanged(nameof(HasVisualPreview));
        }
    }

    public bool HasTextPreview => PreviewKind == PreviewKind.Text && _textPreview is not null;

    /// <summary>True once a real image or text preview is available; drives the symbol fallback.</summary>
    public bool HasVisualPreview => HasThumbnail || HasTextPreview;

    /// <summary>
    /// Loads a decoded thumbnail / text preview once, on first demand. Safe to call repeatedly.
    /// The list asks for the small width; the details pane upgrades to the large width when the
    /// entry is selected, which populates a separate cache key.
    /// </summary>
    public async Task EnsurePreviewAsync(int width, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return;

        // Painting a folder of web images must not fire one download per row. Web entries get
        // their preview when the user selects them or opens quick look.
        if (width == ThumbnailService.ListWidth && VfsPath.IsVirtualPath(FullPath)
            && VfsPath.Parse(FullPath).Scheme is VfsScheme.Http or VfsScheme.Https)
            return;

        if (PreviewKind == PreviewKind.Image)
        {
            if (ThumbnailService.Instance.TryGet(FullPath, width) is { } cached)
            {
                if (width == ThumbnailService.ListWidth || Thumbnail is null) Thumbnail = cached;
                return;
            }
            if (_previewRequested && width == ThumbnailService.ListWidth) return;
            _previewRequested = true;
            var bitmap = await ThumbnailService.Instance.LoadAsync(FullPath, width, cancellationToken);
            if (bitmap is not null && !cancellationToken.IsCancellationRequested &&
                (width == ThumbnailService.ListWidth || Thumbnail is null || width == ThumbnailService.PaneWidth))
            {
                Thumbnail = bitmap;
            }
        }
        else if (PreviewKind == PreviewKind.Text && width == ThumbnailService.PaneWidth)
        {
            if (_textPreview is not null) return;
            var text = await PreviewService.ReadTextPreviewAsync(FullPath, cancellationToken: cancellationToken);
            if (!cancellationToken.IsCancellationRequested) TextPreview = text;
        }
    }

    private static FASymbol PickSymbol(string category) => category switch
    {
        "folder" => FASymbol.FolderFilled,
        "image" => FASymbol.Image,
        "video" => FASymbol.Video,
        "audio" => FASymbol.Audio,
        "archive" => FASymbol.ZipFolder,
        "code" => FASymbol.Code,
        "document" => FASymbol.Document,
        "app" => FASymbol.OpenFolder,
        _ => FASymbol.Page
    };
}
