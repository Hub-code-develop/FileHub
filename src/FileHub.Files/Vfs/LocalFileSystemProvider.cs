// Copyright (c) 2026 Hub-code-develop. Licensed under the MIT License.
//
// Local disk provider. This is the code that used to live directly in FileSystemService, moved
// behind IFileSystemProvider so every other location type gets the same treatment.

using FileHub.Files.Models;

namespace FileHub.Files.Vfs;

public sealed class LocalFileSystemProvider : IFileSystemProvider
{
    public VfsScheme Scheme => VfsScheme.Local;
    public string DisplayName => "Local disk";

    public Task<IReadOnlyList<FileEntry>> ListAsync(VfsPath path, bool includeHidden, CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyList<FileEntry>>(() =>
        {
            var list = new List<FileEntry>();
            var dir = new DirectoryInfo(path.Inner);
            foreach (var item in dir.EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var isHidden = item.Name.StartsWith('.') || (item.Attributes & FileAttributes.Hidden) != 0;
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
                    // One unreadable entry must not fail the whole directory.
                }
            }
            return list;
        }, cancellationToken);
    }

    public Task<VfsStat> StatAsync(VfsPath path, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            try
            {
                if (Directory.Exists(path.Inner))
                {
                    return new VfsStat(true, true, 0,
                        new DateTimeOffset(Directory.GetLastWriteTimeUtc(path.Inner), TimeSpan.Zero));
                }
                if (File.Exists(path.Inner))
                {
                    var info = new FileInfo(path.Inner);
                    return new VfsStat(true, false, info.Length,
                        new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
                }
                return VfsStat.Missing;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return VfsStat.Missing;
            }
        }, cancellationToken);
    }

    public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken)
    {
        // FileShare.ReadWrite so previewing a file that another editor holds open still works.
        Stream stream = new FileStream(path.Inner, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    public Task<Stream> CreateFileAsync(VfsPath path, CancellationToken cancellationToken)
    {
        Stream stream = new FileStream(path.Inner, FileMode.Create, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous);
        return Task.FromResult(stream);
    }

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(path.Inner);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken)
    {
        if (Directory.Exists(path.Inner)) Directory.Delete(path.Inner, recursive);
        else if (File.Exists(path.Inner)) File.Delete(path.Inner);
        return Task.CompletedTask;
    }

    public Task RenameAsync(VfsPath path, string newName, CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(path.Inner)
                     ?? throw new IOException("Cannot locate the parent directory");
        var target = Path.Combine(parent, newName);
        if (Directory.Exists(target) || File.Exists(target))
            throw new IOException("An item with the same name already exists");
        if (Directory.Exists(path.Inner)) Directory.Move(path.Inner, target);
        else File.Move(path.Inner, target);
        return Task.CompletedTask;
    }

    public Task MoveAsync(VfsPath source, VfsPath target, CancellationToken cancellationToken)
    {
        if (Directory.Exists(source.Inner)) Directory.Move(source.Inner, target.Inner);
        else File.Move(source.Inner, target.Inner);
        return Task.CompletedTask;
    }
}
