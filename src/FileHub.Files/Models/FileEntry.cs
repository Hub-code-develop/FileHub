using FileHub.Files.Services;

namespace FileHub.Files.Models;

/// <summary>目录中的一个条目。所有展示用文本都在这里算好，UI 只负责绑定。</summary>
public sealed class FileEntry
{
    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public bool IsHidden { get; init; }
    public long Size { get; init; }
    public DateTimeOffset LastWriteTime { get; init; }

    public string Extension => IsDirectory ? string.Empty : Path.GetExtension(Name);

    public string Kind => IsDirectory
        ? "文件夹"
        : string.IsNullOrWhiteSpace(Extension)
            ? "文件"
            : Extension.TrimStart('.').ToUpperInvariant() + " 文件";

    public string SizeText => IsDirectory ? "—" : FileSystemService.FormatSize(Size);
    public string ModifiedText => LastWriteTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    /// <summary>用于按扩展名挑图标，属于纯数据，映射到具体图标由 UI 层负责。</summary>
    public string Category
    {
        get
        {
            if (IsDirectory) return "folder";
            var ext = Extension.ToLowerInvariant();
            return ext switch
            {
                ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg" or ".heic" or ".tiff" => "image",
                ".mp4" or ".mov" or ".mkv" or ".avi" or ".webm" or ".m4v" => "video",
                ".mp3" or ".wav" or ".flac" or ".aac" or ".ogg" or ".m4a" or ".aiff" => "audio",
                ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz" or ".dmg" or ".iso" => "archive",
                ".cs" or ".xaml" or ".axaml" or ".json" or ".xml" or ".yml" or ".yaml" or ".md" or ".py" or ".js" or ".ts" or ".java" or ".c" or ".cpp" or ".h" or ".sh" => "code",
                ".txt" or ".log" or ".csv" or ".rtf" or ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" => "document",
                ".app" or ".exe" or ".msi" or ".deb" or ".rpm" or ".apk" or ".jar" => "app",
                _ => "file"
            };
        }
    }
}
