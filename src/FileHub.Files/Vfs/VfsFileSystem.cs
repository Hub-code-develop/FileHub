// Copyright (c) 2026 Hub-code-develop. Licensed under the MIT License.
//
// The single entry point the UI uses for every location. Local paths keep using System.IO
// through LocalFileSystemProvider; virtual paths are routed to their provider. Transfers stream
// between providers, which is what makes "extract from zip to a NAS share" a normal drag.

using FileHub.Files.Models;

namespace FileHub.Files.Vfs;

public sealed class VfsFileSystem
{
    private const int CopyBufferSize = 128 * 1024;

    public static VfsFileSystem Default { get; } = new();

    private static readonly LocalFileSystemProvider Local = new();
    private static readonly Dictionary<string, IFileSystemProvider> RemoteProviders = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock ProviderGate = new();

    static VfsFileSystem()
    {
        VfsRegistry.Register(Local);
        VfsRegistry.Register(new ArchiveFileSystemProvider());
        // A URL pasted into the address bar becomes an ordinary location, so plain http(s)
        // paths browse, preview and copy like everything else.
        VfsRegistry.Register(new HttpFileSystemProvider(VfsScheme.Http));
        VfsRegistry.Register(new HttpFileSystemProvider(VfsScheme.Https));
    }

    /// <summary>
    /// Remote providers need credentials, so they are created from the saved location that owns
    /// the path and cached per root. A path whose scheme has no registered provider and no saved
    /// location is reported as unavailable rather than silently browsing nothing.
    /// </summary>
    private static IFileSystemProvider ProviderFor(VfsPath path)
    {
        if (path.IsLocal) return Local;
        if (VfsRegistry.TryResolve(path.Scheme, out var registered)) return registered;

        var root = new VfsPath(path.Scheme, path.Authority, "/").Canonical;
        lock (ProviderGate)
        {
            if (RemoteProviders.TryGetValue(root, out var cached)) return cached;

            var location = RemoteLocationStore.Load()
                .FirstOrDefault(x => string.Equals(x.RootPath, root, StringComparison.OrdinalIgnoreCase));
            if (location is null)
                throw new NotSupportedException(
                    $"No saved location matches {root}. Add it under Network locations first.");

            var provider = location.CreateProvider();
            RemoteProviders[root] = provider;
            return provider;
        }
    }

    /// <summary>Drops cached credentials after a location is edited or removed.</summary>
    public static void ForgetRemoteProvider(string rootPath)
    {
        lock (ProviderGate) RemoteProviders.Remove(rootPath);
    }

    public async Task<IReadOnlyList<FileEntry>> ListAsync(string path, bool includeHidden, CancellationToken ct = default)
    {
        var vp = VfsPath.Parse(path);
        return await ProviderFor(vp).ListAsync(vp, includeHidden, ct).ConfigureAwait(false);
    }

    public async Task<VfsStat> StatAsync(string path, CancellationToken ct = default)
    {
        var vp = VfsPath.Parse(path);
        return await ProviderFor(vp).StatAsync(vp, ct).ConfigureAwait(false);
    }

    public async Task<bool> DirectoryExistsAsync(string path, CancellationToken ct = default)
    {
        if (!VfsPath.IsVirtualPath(path)) return Directory.Exists(path);
        var stat = await StatAsync(path, ct).ConfigureAwait(false);
        return stat.Exists && stat.IsDirectory;
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
    {
        var vp = VfsPath.Parse(path);
        return await ProviderFor(vp).OpenReadAsync(vp, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Breadth-first recursive search. The local provider is fast enough to walk directly;
    /// virtual providers (archive, network) are searched the same way but bounded by maxResults.
    /// </summary>
    public async Task<IReadOnlyList<FileEntry>> SearchAsync(
        string root, string query, int maxResults = 300, CancellationToken ct = default)
    {
        var vp = VfsPath.Parse(root);
        if (vp.IsLocal) return await SearchLocalAsync(root, query, maxResults, ct).ConfigureAwait(false);

        var results = new List<FileEntry>();
        // A website is not a file tree: an index page can link anywhere. Crawling it without a
        // budget would hammer the server, so web search is depth-limited and request-capped.
        var isWeb = vp.Scheme is VfsScheme.Http or VfsScheme.Https;
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        var visited = 0;
        while (queue.Count > 0 && results.Count < maxResults)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = queue.Dequeue();
            if (isWeb && ++visited > 60) break;
            IReadOnlyList<FileEntry> children;
            try { children = await ListAsync(dir, includeHidden: false, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            { continue; }

            foreach (var child in children)
            {
                if (child.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(child);
                    if (results.Count >= maxResults) break;
                }
                if (child.IsDirectory && (!isWeb || depth < 3)) queue.Enqueue((child.FullPath, depth + 1));
            }
        }
        return results;
    }

    private static Task<IReadOnlyList<FileEntry>> SearchLocalAsync(
        string root, string query, int maxResults, CancellationToken ct)
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
                    ct.ThrowIfCancellationRequested();
                    if (list.Count >= maxResults) break;
                    if (!Path.GetFileName(full).Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var isDir = Directory.Exists(full);
                        list.Add(new FileEntry
                        {
                            Name = Path.GetFileName(full),
                            FullPath = full,
                            IsDirectory = isDir,
                            Size = isDir ? 0 : new FileInfo(full).Length,
                            LastWriteTime = new DateTimeOffset(
                                isDir ? Directory.GetLastWriteTimeUtc(full) : File.GetLastWriteTimeUtc(full),
                                TimeSpan.Zero)
                        });
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return list;
        }, ct);
    }

    public async Task<string> CreateFolderAsync(string parent, string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name cannot be empty", nameof(name));
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
            throw new ArgumentException("Name contains invalid characters", nameof(name));

        var parentPath = VfsPath.Parse(parent);
        var target = parentPath.Combine(name);
        if (await StatAsync(target.Canonical, ct).ConfigureAwait(false) is { Exists: true })
            throw new IOException("An item with the same name already exists");
        await ProviderFor(target).CreateDirectoryAsync(target, ct).ConfigureAwait(false);
        return target.Canonical;
    }

    public async Task<string> RenameAsync(string path, string newName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newName)) throw new ArgumentException("Name cannot be empty", nameof(newName));
        var vp = VfsPath.Parse(path);
        if (newName == vp.FileName) return path;
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || newName.Contains('/') || newName.Contains('\\'))
            throw new ArgumentException("Name contains invalid characters", nameof(newName));

        await ProviderFor(vp).RenameAsync(vp, newName, ct).ConfigureAwait(false);
        var parent = vp.Parent ?? vp;
        return parent.Combine(newName).Canonical;
    }

    public async Task DeleteAsync(string path, bool recursive = true, CancellationToken ct = default)
    {
        var vp = VfsPath.Parse(path);
        await ProviderFor(vp).DeleteAsync(vp, recursive, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// True when <paramref name="destinationDir"/> is the same as, or inside, <paramref name="source"/>.
    /// Works across schemes: a local folder cannot be copied into a zip that lives inside itself,
    /// and a remote folder cannot be copied below its own subtree.
    /// </summary>
    public static bool IsDescendantOrSelf(string source, string destinationDir)
    {
        var s = VfsPath.Parse(source).Canonical.TrimEnd('/', '\\');
        var d = VfsPath.Parse(destinationDir).Canonical.TrimEnd('/', '\\');
        if (string.Equals(s, d, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = s + (VfsPath.IsVirtualPath(source) ? "/" : Path.DirectorySeparatorChar.ToString());
        return d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Copies one or more locations into a directory, streaming across providers. Progress
    /// covers bytes actually written so the UI can show a real bar, not a spinner.
    /// </summary>
    public async Task<VfsTransferResult> CopyAsync(
        IReadOnlyList<string> sources,
        string destinationDir,
        VfsConflictPolicy policy = VfsConflictPolicy.Rename,
        Func<string, Task<VfsConflictAnswer>>? askOnConflict = null,
        IProgress<VfsTransferProgress>? progress = null,
        CancellationToken ct = default,
        Func<CancellationToken, Task>? checkpoint = null)
    {
        var result = new VfsTransferResult();
        var total = 0L;
        foreach (var source in sources)
            total += await MeasureAsync(VfsPath.Parse(source), ct).ConfigureAwait(false);

        var done = 0L;
        var index = 0;
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            index++;
            var src = VfsPath.Parse(source);
            try
            {
                var stat = await StatAsync(source, ct).ConfigureAwait(false);
                if (!stat.Exists)
                {
                    result.Errors.Add($"{src.FileName}: source no longer exists");
                    continue;
                }

                var destRoot = VfsPath.Parse(destinationDir);
                var target = destRoot.Combine(src.FileName);
                var existing = await StatAsync(target.Canonical, ct).ConfigureAwait(false);
                if (existing.Exists)
                {
                    var answer = policy switch
                    {
                        VfsConflictPolicy.Overwrite => VfsConflictAnswer.Overwrite,
                        VfsConflictPolicy.Skip => VfsConflictAnswer.Skip,
                        _ => VfsConflictAnswer.KeepBoth
                    };
                    if (askOnConflict is not null)
                    {
                        answer = await askOnConflict(target.Canonical).ConfigureAwait(false);
                        if (answer == VfsConflictAnswer.Cancel)
                        {
                            result.Cancelled = true;
                            break;
                        }
                    }

                    switch (answer)
                    {
                        case VfsConflictAnswer.Skip:
                            result.Skipped++;
                            continue;
                        case VfsConflictAnswer.KeepBoth:
                            target = await EnsureUniqueAsync(destRoot, src.FileName, ct).ConfigureAwait(false);
                            break;
                        case VfsConflictAnswer.Overwrite:
                            await ProviderFor(target).DeleteAsync(target, true, ct).ConfigureAwait(false);
                            break;
                    }
                }

                var state = new TransferState(src.FileName, index, sources.Count, total, done, progress);
                await CopyTreeAsync(src, target, state, ct, checkpoint).ConfigureAwait(false);
                done += await MeasureAsync(src, ct).ConfigureAwait(false);
                progress?.Report(new VfsTransferProgress(src.FileName, done, total, index, sources.Count));
                result.Succeeded++;
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or ArgumentException or NotSupportedException)
            {
                result.Errors.Add($"{src.FileName}: {ex.Message}");
            }
        }

        return result;
    }

    public async Task<VfsTransferResult> MoveAsync(
        IReadOnlyList<string> sources, string destinationDir,
        VfsConflictPolicy policy = VfsConflictPolicy.Rename,
        Func<string, Task<VfsConflictAnswer>>? askOnConflict = null,
        IProgress<VfsTransferProgress>? progress = null,
        CancellationToken ct = default,
        Func<CancellationToken, Task>? checkpoint = null)
    {
        var result = new VfsTransferResult();
        var copied = new List<string>();
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            var src = VfsPath.Parse(source);
            var dest = VfsPath.Parse(destinationDir);
            if (string.Equals(src.Parent?.Canonical, dest.Canonical, StringComparison.OrdinalIgnoreCase))
            {
                result.Skipped++;
                continue;
            }

            // Same container: a provider-side move is instant and preserves metadata.
            if (src.Scheme == dest.Scheme && src.Authority == dest.Authority && src.IsVirtual)
            {
                try
                {
                    await ProviderFor(src).MoveAsync(src, dest.Combine(src.FileName), ct).ConfigureAwait(false);
                    result.Succeeded++;
                    continue;
                }
                catch (NotSupportedException) { }
            }

            if (src.IsLocal && dest.IsLocal)
            {
                try
                {
                    var targetPath = Path.Combine(dest.Inner, src.FileName);
                    if (Directory.Exists(src.Inner)) Directory.Move(src.Inner, targetPath);
                    else File.Move(src.Inner, targetPath);
                    result.Succeeded++;
                    continue;
                }
                catch (IOException)
                {
                    // Cross-volume: fall through to copy + delete below.
                }
            }

            var single = await CopyAsync([source], destinationDir, policy,
                askOnConflict, progress, ct, checkpoint).ConfigureAwait(false);
            if (single.Errors.Count > 0)
            {
                result.Errors.AddRange(single.Errors);
                continue;
            }
            if (single.Succeeded > 0)
            {
                copied.Add(source);
                result.Succeeded++;
            }
        }

        foreach (var source in copied)
        {
            try { await DeleteAsync(source, true, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                result.Errors.Add($"{VfsPath.Parse(source).FileName}: copied but could not remove the source ({ex.Message})");
            }
        }
        return result;
    }

    private sealed class TransferState(
        string item, int index, int count, long total, long done, IProgress<VfsTransferProgress>? progress)
    {
        public void Report(long bytes)
            => progress?.Report(new VfsTransferProgress(item, done + bytes, total, index, count));
    }

    private async Task CopyTreeAsync(
        VfsPath source, VfsPath target, TransferState state, CancellationToken ct,
        Func<CancellationToken, Task>? checkpoint)
    {
        if (checkpoint is not null) await checkpoint(ct).ConfigureAwait(false);

        var stat = await ProviderFor(source).StatAsync(source, ct).ConfigureAwait(false);
        if (!stat.Exists) throw new IOException("Source no longer exists");

        if (!stat.IsDirectory)
        {
            await using var input = await ProviderFor(source).OpenReadAsync(source, ct).ConfigureAwait(false);
            await using var output = await ProviderFor(target).CreateFileAsync(target, ct).ConfigureAwait(false);
            await PumpAsync(input, output, state, ct, checkpoint).ConfigureAwait(false);
            return;
        }

        await ProviderFor(target).CreateDirectoryAsync(target, ct).ConfigureAwait(false);
        var children = await ProviderFor(source).ListAsync(source, includeHidden: true, ct).ConfigureAwait(false);
        foreach (var child in children)
        {
            var childSource = source.Combine(child.Name);
            await CopyTreeAsync(childSource, target.Combine(child.Name), state, ct, checkpoint).ConfigureAwait(false);
        }
    }

    private static async Task PumpAsync(
        Stream input, Stream output, TransferState state, CancellationToken ct,
        Func<CancellationToken, Task>? checkpoint)
    {
        var buffer = new byte[CopyBufferSize];
        long written = 0;
        var sinceReport = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            written += read;
            sinceReport += read;
            // Throttle progress to ~1 MB so a fast SSD copy does not flood the UI thread, and
            // let the queue pause here instead of only between whole files.
            if (sinceReport >= 1024 * 1024)
            {
                sinceReport = 0;
                state.Report(written);
                if (checkpoint is not null) await checkpoint(ct).ConfigureAwait(false);
            }
        }
        state.Report(written);
    }

    private async Task<long> MeasureAsync(VfsPath path, CancellationToken ct)
    {
        var stat = await ProviderFor(path).StatAsync(path, ct).ConfigureAwait(false);
        if (!stat.Exists) return 0;
        if (!stat.IsDirectory) return stat.Size;
        long total = 0;
        var children = await ProviderFor(path).ListAsync(path, includeHidden: true, ct).ConfigureAwait(false);
        foreach (var child in children)
            total += await MeasureAsync(path.Combine(child.Name), ct).ConfigureAwait(false);
        return total;
    }

    private async Task<VfsPath> EnsureUniqueAsync(VfsPath parent, string name, CancellationToken ct)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 1; i < 1000; i++)
        {
            var candidate = parent.Combine($"{stem} ({i}){ext}");
            if (!(await StatAsync(candidate.Canonical, ct).ConfigureAwait(false)).Exists) return candidate;
        }
        return parent.Combine($"{stem} ({Guid.NewGuid():N}){ext}");
    }
}

public sealed class VfsTransferResult
{
    public int Succeeded { get; set; }
    public int Skipped { get; set; }
    public bool Cancelled { get; set; }
    public List<string> Errors { get; } = [];
}
