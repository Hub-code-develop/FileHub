using Cobalt.Files.Models;
using FluentAvalonia.UI.Controls;

namespace Cobalt.Desktop.ViewModels;

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
    public string Kind => Model.Kind;
    public string ModifiedText => Model.ModifiedText;
    public bool IsDirectory => Model.IsDirectory;
    public string Category => Model.Category;

    /// <summary>直接给 FASymbolIcon.Symbol 绑定的枚举值，避免 XAML 里做字符串转换。</summary>
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
