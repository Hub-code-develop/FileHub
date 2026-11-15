namespace Cobalt.Files.Services;

public enum ClipboardOperation
{
    None,
    Copy,
    Cut
}

/// <summary>应用内剪贴板（复制/剪切的文件路径），不碰系统剪贴板。</summary>
public sealed class FileClipboard
{
    public ClipboardOperation Operation { get; private set; } = ClipboardOperation.None;
    public IReadOnlyList<string> Paths { get; private set; } = [];
    public string SourceDirectory { get; private set; } = string.Empty;

    public bool HasContent => Operation != ClipboardOperation.None && Paths.Count > 0;

    public void Set(ClipboardOperation operation, IEnumerable<string> paths, string sourceDirectory)
    {
        Operation = operation;
        Paths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        SourceDirectory = sourceDirectory;
    }

    public void Clear()
    {
        Operation = ClipboardOperation.None;
        Paths = [];
        SourceDirectory = string.Empty;
    }
}
