using FileHub.Files.Models;
using FileHub.Files.Vfs;

namespace FileHub.Files.Services;

/// <summary>
/// Presentation helpers plus the legacy static façade over <see cref="VfsFileSystem"/>. New code
/// should call <see cref="VfsFileSystem.Default"/> directly; these forwards exist so the desktop
/// layer keeps one obvious entry point for local and virtual locations alike.
/// </summary>
public static class FileSystemService
{
    private static readonly string[] TextSizeUnits = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string FormatSize(long bytes)
    {
        if (bytes < 0) return "—";
        if (bytes < 1024) return bytes + " B";
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < TextSizeUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 1 ? $"{value:0.#} {TextSizeUnits[unit]}" : $"{value:0.##} {TextSizeUnits[unit]}";
    }

    /// <summary>左侧栏：固定快速访问位置 + 当前已挂载的卷（跨平台）。</summary>
    public static IReadOnlyList<LocationEntry> GetLocations()
    {
        var result = new List<LocationEntry>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        void AddQuick(string nameKey, string? path, string icon)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path) &&
                !result.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(new LocationEntry { Name = nameKey, NameKey = nameKey, Path = path, IconKey = icon, Kind = LocationKind.QuickAccess });
            }
        }

        AddQuick("LocHome", home, "Home");
        AddQuick("LocDesktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "View");
        AddQuick("LocDownloads", Path.Combine(home, "Downloads"), "Download");
        AddQuick("LocDocuments", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Document");
        AddQuick("LocPictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Picture");
        AddQuick("LocVideos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Video");

        foreach (var drive in GetDrives())
        {
            var name = drive.RootDirectory.FullName.TrimEnd(Path.DirectorySeparatorChar);
            // The macOS boot volume is "/" whose trimmed name is empty, and "/" alone is a poor
            // sidebar label, so fall back to the conventional volume name there.
            if (string.IsNullOrWhiteSpace(name)) name = OperatingSystem.IsMacOS() ? "Macintosh HD" : "/";

            // macOS reports "/" (or "\" on Windows) as the VolumeLabel of a root volume, which
            // would render as "/ (Macintosh HD)". Treat a bare separator as "no label".
            var label = drive.VolumeLabel;
            if (label is "/" or "\\" || string.IsNullOrWhiteSpace(label)) label = null;

            result.Add(new LocationEntry
            {
                Name = label is null ? name : $"{label} ({name})",
                Path = drive.RootDirectory.FullName,
                IconKey = "HardDrive",
                Kind = LocationKind.Drive,
                Detail = drive.IsReady
                    ? $"{FormatSize(drive.AvailableFreeSpace)} / {FormatSize(drive.TotalSize)}"
                    : null
            });
        }

        return result;
    }

    private static IEnumerable<DriveInfo> GetDrives()
    {
        var skip = new[] { "/proc", "/sys", "/dev", "/run", "/private/var", "/System/Volumes" };
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Where(d => !skip.Any(s => d.RootDirectory.FullName.StartsWith(s, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(d => d.RootDirectory.FullName)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return [];
        }
    }

    public static Task<IReadOnlyList<FileEntry>> ListDirectoryAsync(
        string path, bool includeHidden, CancellationToken cancellationToken = default)
        => VfsFileSystem.Default.ListAsync(path, includeHidden, cancellationToken);

    public static Task<IReadOnlyList<FileEntry>> SearchAsync(
        string root, string query, int maxResults = 300, CancellationToken cancellationToken = default)
        => VfsFileSystem.Default.SearchAsync(root, query, maxResults, cancellationToken);

    public static Task<string> CreateFolderAsync(string parent, string name, CancellationToken ct = default)
        => VfsFileSystem.Default.CreateFolderAsync(parent, name, ct);

    public static Task<string> RenameAsync(string source, string newName, CancellationToken ct = default)
        => VfsFileSystem.Default.RenameAsync(source, newName, ct);

    public static Task<VfsTransferResult> CopyIntoAsync(
        IReadOnlyList<string> sources, string destinationDir,
        IProgress<VfsTransferProgress>? progress = null, CancellationToken ct = default)
        => VfsFileSystem.Default.CopyAsync(sources, destinationDir, VfsConflictPolicy.Rename, null, progress, ct);

    public static Task<VfsTransferResult> MoveIntoAsync(
        IReadOnlyList<string> sources, string destinationDir,
        IProgress<VfsTransferProgress>? progress = null, CancellationToken ct = default)
        => VfsFileSystem.Default.MoveAsync(sources, destinationDir, VfsConflictPolicy.Rename, null, progress, ct);

    /// <summary>
    /// True when moving/copying <paramref name="source"/> would place it inside itself.
    /// File managers must reject this case: a copy would recurse forever.
    /// </summary>
    public static bool IsDescendantOrSelf(string source, string destinationDir)
        => VfsFileSystem.IsDescendantOrSelf(source, destinationDir);
}
