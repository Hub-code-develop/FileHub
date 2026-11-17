using FileHub.Files.Models;

namespace FileHub.Files.Services;

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

    /// <summary>异步列出目录，单个条目权限异常被跳过而不是让整个目录失败。</summary>
    public static Task<IReadOnlyList<FileEntry>> ListDirectoryAsync(
        string path, bool includeHidden, CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<FileEntry>>(() =>
        {
            var list = new List<FileEntry>();
            var dir = new DirectoryInfo(path);
            foreach (var item in dir.EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var isHidden = item.Name.StartsWith('.') ||
                                   (item.Attributes & FileAttributes.Hidden) != 0;
                    if (isHidden && !includeHidden) continue;

                    var isDir = item is DirectoryInfo;
                    list.Add(new FileEntry
                    {
                        Name = item.Name,
                        FullPath = item.FullName,
                        IsDirectory = isDir,
                        IsHidden = isHidden,
                        Size = isDir ? 0 : new FileInfo(item.FullName).Length,
                        LastWriteTime = new DateTimeOffset(item.LastWriteTimeUtc, TimeSpan.Zero)
                    });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 单个文件读不到元数据不影响其余条目
                }
            }
            return list;
        }, cancellationToken);
    }

    /// <summary>递归搜索，带结果上限，避免在大目录里失控。</summary>
    public static Task<IReadOnlyList<FileEntry>> SearchAsync(
        string root, string query, int maxResults = 300, CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<FileEntry>>(() =>
        {
            var list = new List<FileEntry>();
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                ReturnSpecialDirectories = false
            };

            try
            {
                foreach (var full in Directory.EnumerateFileSystemEntries(root, "*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (list.Count >= maxResults) break;
                    if (!Path.GetFileName(full).Contains(query, StringComparison.OrdinalIgnoreCase)) continue;

                    try
                    {
                        var info = new FileInfo(full);
                        var isDir = Directory.Exists(full);
                        list.Add(new FileEntry
                        {
                            Name = Path.GetFileName(full),
                            FullPath = full,
                            IsDirectory = isDir,
                            Size = isDir ? 0 : info.Length,
                            LastWriteTime = new DateTimeOffset(
                                (isDir ? new DirectoryInfo(full).LastWriteTimeUtc : info.LastWriteTimeUtc),
                                TimeSpan.Zero)
                        });
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            return list;
        }, cancellationToken);
    }

    public static string CreateFolder(string parent, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name cannot be empty", nameof(name));
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("Name contains invalid characters", nameof(name));
        var target = Path.Combine(parent, name);
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("An item with the same name already exists");
        Directory.CreateDirectory(target);
        return target;
    }

    public static string Rename(string source, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) throw new ArgumentException("Name cannot be empty", nameof(newName));
        if (newName == Path.GetFileName(source)) return source;
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("Name contains invalid characters", nameof(newName));
        var parent = Path.GetDirectoryName(source) ?? throw new IOException("Cannot locate the parent directory");
        var target = Path.Combine(parent, newName);
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("An item with the same name already exists");
        if (Directory.Exists(source)) Directory.Move(source, target);
        else File.Move(source, target);
        return target;
    }

    public static void CopyInto(IEnumerable<string> sources, string destinationDir, CancellationToken ct = default)
    {
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(destinationDir, Path.GetFileName(source));
            target = EnsureUnique(target);
            if (Directory.Exists(source)) CopyDirectory(source, target, ct);
            else File.Copy(source, target);
        }
    }

    public static void MoveInto(IEnumerable<string> sources, string destinationDir, CancellationToken ct = default)
    {
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(destinationDir, Path.GetFileName(source));
            if (string.Equals(Path.GetDirectoryName(source), destinationDir, StringComparison.OrdinalIgnoreCase)) continue;
            target = EnsureUnique(target);
            if (Directory.Exists(source)) Directory.Move(source, target);
            else File.Move(source, target);
        }
    }

    private static void CopyDirectory(string source, string target, CancellationToken ct)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            ct.ThrowIfCancellationRequested();
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: false);
        }
        foreach (var sub in Directory.EnumerateDirectories(source))
        {
            ct.ThrowIfCancellationRequested();
            CopyDirectory(sub, Path.Combine(target, Path.GetFileName(sub)), ct);
        }
    }

    private static string EnsureUnique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{name} ({Guid.NewGuid():N}){ext}");
    }
}
