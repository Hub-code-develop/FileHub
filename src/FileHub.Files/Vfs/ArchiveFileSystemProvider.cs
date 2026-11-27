// Copyright (c) 2026 Hub-code-develop. Licensed under the MIT License.
//
// Browses an archive as a folder tree. Zip/7z/rar/tar are read through SharpCompress, and a
// compressed tar (tar.gz, tar.bz2, tar.xz) is unwrapped on the fly, so double-clicking a .tgz
// shows its folders instead of one opaque blob.
//
// A zip rarely stores explicit directory entries, so the tree is synthesised from entry keys.
// Building that tree is the expensive part (a 50k-entry archive costs one central-directory
// scan), therefore it is cached per container and invalidated by length + last-write time.
//
// The container stays read-only here on purpose: rewriting a zip in place would have to copy
// every byte anyway, so FileHub routes that through ArchiveService (extract, edit, repack).

using System.Collections.Concurrent;
using FileHub.Files.Models;
using FileHub.Files.Services;

namespace FileHub.Files.Vfs;

/// <summary>
/// Exposes an archive file as a read-only directory tree. Virtual paths take the form
/// <c>archive:///path/pack.zip!/folder/file.txt</c>.
/// </summary>
public sealed class ArchiveFileSystemProvider : IFileSystemProvider
{
    /// <summary>Entries up to this size are returned from memory; larger ones spill to a temp file.</summary>
    private const long MemoryBufferLimit = 64L * 1024 * 1024;

    private const int MaxCachedContainers = 24;

    private static readonly ConcurrentDictionary<string, ContainerCache> Cache =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Supplies the password for an encrypted container. The argument is the absolute container
    /// path; returning null means "no password", which makes browsing raise
    /// <c>ARCHIVE_PASSWORD_REQUIRED</c> instead of failing with a crypto error.
    /// </summary>
    public static Func<string, string?>? PasswordProvider { get; set; }

    /// <summary>Always <see cref="VfsScheme.Archive"/>.</summary>
    public VfsScheme Scheme => VfsScheme.Archive;

    /// <summary>Label shown for archive locations in the sidebar.</summary>
    public string DisplayName => "Archive";

    /// <summary>Lists one folder of the archive, including folders that only exist implicitly.</summary>
    public async Task<IReadOnlyList<FileEntry>> ListAsync(
        VfsPath path, bool includeHidden, CancellationToken cancellationToken)
    {
        var container = ContainerOf(path);
        var snapshot = await GetSnapshotAsync(container, cancellationToken).ConfigureAwait(false);
        var directory = ArchiveIo.NormalizeKey(path.Inner);

        if (!snapshot.Children.TryGetValue(directory, out var children))
        {
            throw new DirectoryNotFoundException(
                $"'{path.Canonical}' is not a folder inside '{Path.GetFileName(container)}'.");
        }

        var result = new List<FileEntry>(children.Count);
        foreach (var child in children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hidden = IsHiddenName(child.Name);
            if (hidden && !includeHidden) continue;

            result.Add(new FileEntry
            {
                Name = child.Name,
                FullPath = FullPathOf(container, child.Path),
                IsDirectory = child.IsDirectory,
                IsHidden = hidden,
                Size = child.IsDirectory ? 0 : child.Size,
                LastWriteTime = child.Modified
            });
        }

        return result;
    }

    /// <summary>Reports whether a folder or file exists inside the container.</summary>
    public async Task<VfsStat> StatAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var container = ContainerOf(path);
        if (!File.Exists(container)) return VfsStat.Missing;

        var snapshot = await GetSnapshotAsync(container, cancellationToken).ConfigureAwait(false);
        var key = ArchiveIo.NormalizeKey(path.Inner);
        if (!snapshot.Nodes.TryGetValue(key, out var node)) return VfsStat.Missing;

        return new VfsStat(true, node.IsDirectory, node.IsDirectory ? 0 : node.Size, node.Modified);
    }

    /// <summary>
    /// Returns a self-contained, seekable stream. SharpCompress entry streams die with their
    /// archive and are forward-only, so the entry is copied out first: into memory for small
    /// files, into a delete-on-close temp file for large ones. Nothing holds the archive open
    /// after this returns, which keeps previews of a locked zip working.
    /// </summary>
    public async Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var container = ContainerOf(path);
        var snapshot = await GetSnapshotAsync(container, cancellationToken).ConfigureAwait(false);
        var key = ArchiveIo.NormalizeKey(path.Inner);

        if (!snapshot.Nodes.TryGetValue(key, out var node))
        {
            throw new FileNotFoundException(
                $"'{path.Canonical}' was not found inside '{Path.GetFileName(container)}'.");
        }

        if (node.IsDirectory)
        {
            throw new InvalidOperationException($"'{node.Name}' is a folder inside the archive.");
        }

        var password = NormalizePassword(PasswordProvider?.Invoke(container));
        return await Task.Run(
            () => BufferEntryAsync(container, password, snapshot.Mode, node, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Archives cannot be written in place; repack them with <see cref="ArchiveService"/>.</summary>
    public Task<Stream> CreateFileAsync(VfsPath path, CancellationToken cancellationToken)
        => throw ReadOnly("Creating a file", path);

    /// <summary>Archives cannot be written in place; repack them with <see cref="ArchiveService"/>.</summary>
    public Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken)
        => throw ReadOnly("Creating a folder", path);

    /// <summary>Archives cannot be written in place; repack them with <see cref="ArchiveService"/>.</summary>
    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken)
        => throw ReadOnly("Deleting", path);

    /// <summary>Archives cannot be written in place; repack them with <see cref="ArchiveService"/>.</summary>
    public Task RenameAsync(VfsPath path, string newName, CancellationToken cancellationToken)
        => throw ReadOnly("Renaming", path);

    /// <summary>Archives cannot be written in place; repack them with <see cref="ArchiveService"/>.</summary>
    public Task MoveAsync(VfsPath source, VfsPath target, CancellationToken cancellationToken)
        => throw ReadOnly("Moving", source);

    /// <summary>Reads the entry table once so the first folder listing is instant.</summary>
    public async Task PrimeAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var container = ContainerOf(path);
        await GetSnapshotAsync(container, cancellationToken).ConfigureAwait(false);
    }

    private static string ContainerOf(VfsPath path)
    {
        if (path.Scheme != VfsScheme.Archive)
            throw new ArgumentException($"'{path.Scheme}' paths are not handled by {nameof(ArchiveFileSystemProvider)}.", nameof(path));
        if (string.IsNullOrWhiteSpace(path.Authority))
            throw new ArgumentException("An archive path must name its container file.", nameof(path));
        return path.Authority;
    }

    private static string FullPathOf(string container, string innerPath)
        => new VfsPath(VfsScheme.Archive, container, innerPath.Length == 0 ? "/" : "/" + innerPath).Canonical;

    private static bool IsHiddenName(string name)
        => name.Length > 0 && name[0] == '.';

    private static string? NormalizePassword(string? password)
        => string.IsNullOrEmpty(password) ? null : password;

    private static NotSupportedException ReadOnly(string operation, VfsPath path)
    {
        var name = Path.GetFileName(path.Authority);
        return new NotSupportedException(
            $"{operation} inside '{name}' is not supported: archives are read-only containers. " +
            "Extract the entry, change it, then create a new archive with ArchiveService.");
    }

    private static async Task<ArchiveSnapshot> GetSnapshotAsync(string container, CancellationToken ct)
    {
        var full = Path.GetFullPath(container);
        var info = new FileInfo(full);
        if (!info.Exists)
            throw new FileNotFoundException($"The archive '{full}' does not exist.", full);

        var password = NormalizePassword(PasswordProvider?.Invoke(full));
        var cache = Cache.GetOrAdd(full, static _ => new ContainerCache());

        await cache.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref cache.LastUsedTicks, DateTime.UtcNow.Ticks);

            var cached = cache.Snapshot;
            if (cached is not null &&
                cached.Length == info.Length &&
                cached.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks &&
                string.Equals(cached.Password, password, StringComparison.Ordinal))
            {
                return cached;
            }

            var snapshot = await Task.Run(() => BuildSnapshot(full, info, password, ct), ct)
                .ConfigureAwait(false);
            cache.Snapshot = snapshot;
            TrimCache();
            return snapshot;
        }
        finally
        {
            cache.Gate.Release();
        }
    }

    /// <summary>
    /// Turns the flat entry list into a directory tree. Folders come from explicit directory
    /// entries and from the path prefix of every file, which is what a zip usually relies on.
    /// </summary>
    private static ArchiveSnapshot BuildSnapshot(
        string container, FileInfo info, string? password, CancellationToken ct)
    {
        var fallback = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        var contents = ArchiveIo.ReadContents(container, password, fallback, ct);

        var snapshot = new ArchiveSnapshot
        {
            Length = info.Length,
            LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
            Password = password,
            Mode = contents.Mode
        };

        var rootName = Path.GetFileName(container);
        snapshot.Nodes[string.Empty] = new ArchiveNode
        {
            Name = rootName.Length == 0 ? container : rootName,
            Path = string.Empty,
            IsDirectory = true,
            Modified = fallback
        };
        snapshot.Children[string.Empty] = [];

        foreach (var entry in contents.Entries)
        {
            ct.ThrowIfCancellationRequested();

            var path = entry.Path;
            if (path.Length == 0)
            {
                // A single-file compressor (.gz/.bz2/.xz) has one nameless entry; name it after
                // the container so it shows up as "notes.txt", not as an empty row.
                if (entry.IsDirectory) continue;
                path = ArchiveIo.NormalizeKey(ArchiveIo.SingleFileName(container));
                if (path.Length == 0) continue;
            }

            if (entry.IsDirectory)
            {
                EnsureDirectory(snapshot, path, entry.Modified);
            }
            else
            {
                AddFile(snapshot, path, entry.Size, entry.Modified, entry.Key);
            }
        }

        foreach (var list in snapshot.Children.Values)
        {
            list.Sort(static (left, right) =>
            {
                if (left.IsDirectory != right.IsDirectory) return left.IsDirectory ? -1 : 1;
                return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            });
        }

        return snapshot;
    }

    private static void EnsureDirectory(ArchiveSnapshot snapshot, string path, DateTimeOffset modified)
    {
        var separator = path.LastIndexOf('/');
        var parent = separator < 0 ? string.Empty : path[..separator];
        var name = separator < 0 ? path : path[(separator + 1)..];

        if (snapshot.Nodes.TryGetValue(path, out var existing))
        {
            // An explicit directory entry may carry a better timestamp than the implicit one.
            if (existing.IsDirectory && modified > existing.Modified) existing.Modified = modified;
            return;
        }

        if (parent.Length > 0) EnsureDirectory(snapshot, parent, modified);

        var node = new ArchiveNode
        {
            Name = name,
            Path = path,
            IsDirectory = true,
            Modified = modified
        };
        snapshot.Nodes[path] = node;

        if (!snapshot.Children.TryGetValue(parent, out var siblings))
        {
            siblings = [];
            snapshot.Children[parent] = siblings;
        }
        siblings.Add(node);
    }

    private static void AddFile(
        ArchiveSnapshot snapshot, string path, long size, DateTimeOffset modified, string entryKey)
    {
        var separator = path.LastIndexOf('/');
        var parent = separator < 0 ? string.Empty : path[..separator];
        var name = separator < 0 ? path : path[(separator + 1)..];
        if (name.Length == 0) return;

        if (parent.Length > 0) EnsureDirectory(snapshot, parent, modified);

        if (snapshot.Nodes.TryGetValue(path, out var existing))
        {
            if (existing.IsDirectory) return;
            existing.Size = size;
            existing.Modified = modified;
            return;
        }

        var node = new ArchiveNode
        {
            Name = name,
            Path = path,
            IsDirectory = false,
            Size = size,
            Modified = modified,
            EntryKey = entryKey
        };
        snapshot.Nodes[path] = node;

        if (!snapshot.Children.TryGetValue(parent, out var siblings))
        {
            siblings = [];
            snapshot.Children[parent] = siblings;
        }
        siblings.Add(node);
    }

    private static async Task<Stream> BufferEntryAsync(
        string container, string? password, ArchiveReadMode mode, ArchiveNode node, CancellationToken ct)
    {
        // A streamed source reports Size 0 for formats that store it in a trailer, so do not
        // trust an unknown length to mean "empty".
        var unknownSize = mode == ArchiveReadMode.Streamed && node.Size <= 0;
        if (unknownSize || node.Size > MemoryBufferLimit) return await CopyToTempFileAsync(container, password, mode, node, ct).ConfigureAwait(false);

        var memory = new MemoryStream(node.Size is > 0 and < int.MaxValue ? (int)node.Size : 0);
        try
        {
            await ArchiveIo.CopyEntryToAsync(container, password, mode, node.Path, memory, null, ct)
                .ConfigureAwait(false);

            if (memory.Length <= MemoryBufferLimit)
            {
                memory.Position = 0;
                return memory;
            }
        }
        catch
        {
            await memory.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        try
        {
            memory.Position = 0;
            var temp = CreateTempFileStream();
            try
            {
                await memory.CopyToAsync(temp, ct).ConfigureAwait(false);
                temp.Position = 0;
                return temp;
            }
            catch
            {
                await temp.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            await memory.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<Stream> CopyToTempFileAsync(
        string container, string? password, ArchiveReadMode mode, ArchiveNode node, CancellationToken ct)
    {
        var temp = CreateTempFileStream();
        try
        {
            await ArchiveIo.CopyEntryToAsync(container, password, mode, node.Path, temp, null, ct)
                .ConfigureAwait(false);
            temp.Position = 0;
            return temp;
        }
        catch
        {
            await temp.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static FileStream CreateTempFileStream()
    {
        var path = Path.Combine(Path.GetTempPath(), $"filehub-archive-{Guid.NewGuid():N}.tmp");
        return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            128 * 1024, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
    }

    private static void TrimCache()
    {
        if (Cache.Count <= MaxCachedContainers) return;

        // Drop containers that no longer exist first, then the least recently used ones.
        foreach (var pair in Cache)
        {
            if (Cache.Count <= MaxCachedContainers) return;
            if (!File.Exists(pair.Key)) Cache.TryRemove(pair.Key, out _);
        }

        while (Cache.Count > MaxCachedContainers)
        {
            var oldestKey = default(string);
            var oldestTicks = long.MaxValue;
            foreach (var pair in Cache)
            {
                var ticks = Volatile.Read(ref pair.Value.LastUsedTicks);
                if (ticks >= oldestTicks) continue;
                oldestTicks = ticks;
                oldestKey = pair.Key;
            }

            if (oldestKey is null) return;
            Cache.TryRemove(oldestKey, out _);
        }
    }

    private sealed class ContainerCache
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public ArchiveSnapshot? Snapshot;
        public long LastUsedTicks;
    }

    private sealed class ArchiveSnapshot
    {
        public long Length { get; init; }
        public long LastWriteUtcTicks { get; init; }
        public string? Password { get; init; }
        public ArchiveReadMode Mode { get; init; }
        public Dictionary<string, ArchiveNode> Nodes { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<ArchiveNode>> Children { get; } = new(StringComparer.Ordinal);
    }

    private sealed class ArchiveNode
    {
        public string Name { get; init; } = string.Empty;

        /// <summary>Normalised path inside the archive; "" is the archive root.</summary>
        public string Path { get; init; } = string.Empty;

        public bool IsDirectory { get; init; }
        public long Size { get; set; }
        public DateTimeOffset Modified { get; set; }

        /// <summary>Key as stored in the container, used when the entry is opened again.</summary>
        public string EntryKey { get; init; } = string.Empty;
    }
}
