using FileHub.Desktop.Services;
using FileHub.Files.Models;
using FluentAvalonia.UI.Controls;

namespace FileHub.Desktop.ViewModels;

public sealed class FileEntryViewModel
{
    public FileEntryViewModel(FileEntry model)
    {
        Model = model;
        Symbol = PickSymbol(model.Category);
    }

    public FileEntry Model { get; }

    public string Name => Model.Name;
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
