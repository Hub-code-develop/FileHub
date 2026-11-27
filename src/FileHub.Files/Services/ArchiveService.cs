// Copyright (c) 2026 Hub-code-develop. Licensed under the MIT License.
//
// Archive creation and extraction. Archives are read-only containers while browsing (see
// ArchiveFileSystemProvider); every real write goes through this service: extract, change,
// then repack. Reading is delegated to SharpCompress, which handles zip/7z/rar/tar in one API
// and, through ReaderFactory, transparently unwraps gzip/bzip2/xz compressed tars.

using System.Buffers;
using FileHub.Files.Vfs;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Common.Options;
using SharpCompress.Readers;
using SharpCompress.Writers;
using SharpCompress.Writers.SevenZip;
using ArchiveCryptoException = SharpCompress.Common.CryptographicException;
using SharpCompressException = SharpCompress.Common.SharpCompressException;

namespace FileHub.Files.Services;

/// <summary>Archive formats FileHub is able to create.</summary>
public enum ArchiveFormat
{
    Zip,
    Tar,
    TarGz,
    SevenZip
}

/// <summary>
/// Creates and extracts archives. Extraction never overwrites an existing file; a clashing name
/// gets a " (1)", " (2)" ... suffix instead. Password support is read-only: SharpCompress 0.50.4
/// can open encrypted zip/7z files but cannot write them, so <see cref="CreateAsync"/> rejects a
/// password instead of silently producing an unprotected archive.
/// </summary>
public static class ArchiveService
{
    private const int CopyBufferSize = 128 * 1024;

    /// <summary>
    /// Packs files and folders into a new archive, preserving each source's path relative to its
    /// parent folder. Directories are written recursively; symbolic-link loops are skipped.
    /// </summary>
    /// <param name="sourcePaths">Files or folders to add, in order.</param>
    /// <param name="targetFile">Destination archive path. An existing file is never overwritten;
    /// a " (1)" suffix is appended instead.</param>
    /// <param name="format">Container format to write.</param>
    /// <param name="password">Not supported for writing; must be null or empty.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The path of the archive that was actually written.</returns>
    /// <exception cref="NotSupportedException">A password was supplied.</exception>
    /// <exception cref="FileNotFoundException">A source path does not exist.</exception>
    public static async Task<string> CreateAsync(
        IReadOnlyList<string> sourcePaths,
        string targetFile,
        ArchiveFormat format,
        string? password,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        if (string.IsNullOrWhiteSpace(targetFile))
            throw new ArgumentException("Target file is required.", nameof(targetFile));
        if (sourcePaths.Count == 0)
            throw new ArgumentException("At least one source path is required.", nameof(sourcePaths));
        if (!string.IsNullOrEmpty(password))
        {
            throw new NotSupportedException(
                "SharpCompress cannot create password-protected archives. Pass a null password, " +
                "or encrypt the archive with an external tool after creating it.");
        }

        var target = Path.GetFullPath(targetFile);
        var parent = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        target = EnsureUniqueFile(target);

        try
        {
            await Task.Run(() =>
            {
                var items = CollectItems(sourcePaths, ct);
                WriteItems(items, target, format, ct);
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(target);
            throw;
        }

        return target;
    }

    /// <summary>
    /// Extracts an archive, or one folder/file inside it, into a destination directory.
    /// Existing files are kept and the incoming copy is renamed with a " (n)" suffix.
    /// </summary>
    /// <param name="archiveFile">Archive to read.</param>
    /// <param name="innerPath">Folder or file inside the archive ("/" or "" for the whole
    /// archive). When a folder is selected its own name is preserved under the destination.</param>
    /// <param name="destinationDir">Directory that receives the entries; created if missing.</param>
    /// <param name="password">Password for an encrypted archive, or null.</param>
    /// <param name="progress">Optional byte-level progress for the UI.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of files written.</returns>
    /// <exception cref="InvalidOperationException">The archive needs a password that was not
    /// supplied (message "ARCHIVE_PASSWORD_REQUIRED") or the password is wrong.</exception>
    /// <exception cref="InvalidDataException">The archive is damaged, uses an unsupported format,
    /// or contains an entry that would escape the destination folder.</exception>
    public static async Task<int> ExtractAsync(
        string archiveFile,
        string innerPath,
        string destinationDir,
        string? password,
        IProgress<VfsTransferProgress>? progress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(archiveFile))
            throw new ArgumentException("Archive path is required.", nameof(archiveFile));
        if (string.IsNullOrWhiteSpace(destinationDir))
            throw new ArgumentException("Destination directory is required.", nameof(destinationDir));

        var container = Path.GetFullPath(archiveFile);
        if (!File.Exists(container))
            throw new FileNotFoundException($"The archive '{container}' does not exist.", container);

        var destination = Path.GetFullPath(destinationDir);
        Directory.CreateDirectory(destination);

        var prefix = ArchiveIo.NormalizeKey(innerPath);
        var parentPrefix = ParentOf(prefix);
        var secret = ArchiveIo.NormalizePassword(password);
        var stamp = new DateTimeOffset(File.GetLastWriteTimeUtc(container), TimeSpan.Zero);

        // A metadata pass gives an accurate total for the progress bar. For zip/7z/rar this
        // reads the central directory only; for a compressed tar it wraps the archive once.
        var plan = await Task.Run(() => ArchiveIo.ReadContents(container, secret, stamp, ct), ct)
            .ConfigureAwait(false);

        var selection = new List<(ArchiveEntryInfo Entry, string Relative)>();
        foreach (var entry in plan.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var path = entry.Path.Length == 0 ? ArchiveIo.SingleFileName(container) : entry.Path;
            if (!TrySelect(path, prefix, parentPrefix, out var relative)) continue;
            selection.Add((entry, relative));
        }

        if (selection.Count == 0)
        {
            throw new FileNotFoundException(
                $"'{innerPath}' was not found inside '{Path.GetFileName(container)}'.", innerPath);
        }

        var totalBytes = selection.Where(x => !x.Entry.IsDirectory).Sum(x => x.Entry.Size);
        var itemCount = selection.Count;
        var doneBytes = 0L;
        var filesWritten = 0;
        var index = 0;

        void Report(string itemName)
            => progress?.Report(new VfsTransferProgress(itemName, doneBytes, totalBytes, index, itemCount));

        await Task.Run(async () =>
        {
            if (plan.Mode == ArchiveReadMode.Direct)
            {
                using var fs = ArchiveIo.OpenContainer(container);
                using var archive = ArchiveFactory.OpenArchive(fs, ArchiveIo.Options(secret, leaveOpen: true));
                foreach (var entry in archive.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var path = ArchiveIo.NormalizeKey(entry.Key);
                    if (path.Length == 0) path = ArchiveIo.SingleFileName(container);
                    if (!TrySelect(path, prefix, parentPrefix, out var relative)) continue;

                    index++;
                    if (entry.IsDirectory)
                    {
                        Directory.CreateDirectory(ResolveUnder(destination, relative));
                        Report(NameOf(relative));
                        continue;
                    }

                    if (entry.IsEncrypted && secret is null) throw ArchiveIo.PasswordRequired(container);
                    using var source = entry.OpenEntryStream();
                    var target = ResolveUnder(destination, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    target = EnsureUniqueFile(target);

                    var name = NameOf(relative);
                    var written = await WriteEntryAsync(target, source, doneBytes, totalBytes, name, index, itemCount, progress, ct)
                        .ConfigureAwait(false);
                    doneBytes += written;
                    filesWritten++;
                    Report(name);
                }
            }
            else
            {
                using var fs = ArchiveIo.OpenContainer(container);
                using var reader = ReaderFactory.OpenReader(fs, ArchiveIo.Options(secret, leaveOpen: true));
                while (reader.MoveToNextEntry())
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = reader.Entry;
                    var path = ArchiveIo.NormalizeKey(entry.Key);
                    if (path.Length == 0) path = ArchiveIo.SingleFileName(container);
                    if (!TrySelect(path, prefix, parentPrefix, out var relative)) continue;

                    index++;
                    if (entry.IsDirectory)
                    {
                        Directory.CreateDirectory(ResolveUnder(destination, relative));
                        Report(NameOf(relative));
                        continue;
                    }

                    if (entry.IsEncrypted && secret is null) throw ArchiveIo.PasswordRequired(container);
                    using var source = reader.OpenEntryStream();
                    var target = ResolveUnder(destination, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    target = EnsureUniqueFile(target);

                    var name = NameOf(relative);
                    var written = await WriteEntryAsync(target, source, doneBytes, totalBytes, name, index, itemCount, progress, ct)
                        .ConfigureAwait(false);
                    doneBytes += written;
                    filesWritten++;
                    Report(name);
                }
            }
        }, ct).ConfigureAwait(false);

        return filesWritten;
    }

    /// <summary>True when the file name ends with a known archive or compression extension.</summary>
    public static bool IsArchiveExtension(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = Path.GetExtension(path);
        if (ext.Length == 0) return false;
        return ext.ToLowerInvariant() switch
        {
            ".zip" or ".tar" or ".gz" or ".tgz" or ".bz2" or ".xz" or ".7z" or ".rar"
                or ".tbz" or ".tbz2" or ".txz" or ".zst" => true,
            _ => false
        };
    }

    /// <summary>
    /// Best guess of the format to use when packing to this file name. Returns null for formats
    /// FileHub can only read (rar, single-file gzip) or for unknown extensions.
    /// </summary>
    public static ArchiveFormat? GuessFormat(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var lower = path.ToLowerInvariant();
        if (lower.EndsWith(".tar.gz", StringComparison.Ordinal) || lower.EndsWith(".tgz", StringComparison.Ordinal))
            return ArchiveFormat.TarGz;

        return Path.GetExtension(lower) switch
        {
            ".zip" => ArchiveFormat.Zip,
            ".tar" => ArchiveFormat.Tar,
            ".7z" => ArchiveFormat.SevenZip,
            _ => null
        };
    }

    private static async Task<long> WriteEntryAsync(
        string target,
        Stream source,
        long bytesDoneSoFar,
        long totalBytes,
        string itemName,
        int index,
        int itemCount,
        IProgress<VfsTransferProgress>? progress,
        CancellationToken ct)
    {
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, CopyBufferSize, FileOptions.Asynchronous);

        // Throttle to ~1 MB so a fast disk does not flood the UI thread with progress events.
        var lastReported = 0L;
        void OnBytes(long bytes)
        {
            if (progress is null || bytes - lastReported < 1024 * 1024) return;
            lastReported = bytes;
            progress.Report(new VfsTransferProgress(itemName, bytesDoneSoFar + bytes, totalBytes, index, itemCount));
        }

        var written = await ArchiveIo.PumpAsync(source, output, OnBytes, ct).ConfigureAwait(false);
        progress?.Report(new VfsTransferProgress(itemName, bytesDoneSoFar + written, totalBytes, index, itemCount));
        return written;
    }

    /// <summary>
    /// Decides whether an entry belongs to the selection. Selecting a folder keeps the folder's
    /// own name, so "docs" extracted into "/tmp/x" produces "/tmp/x/docs/...".
    /// </summary>
    private static bool TrySelect(string path, string prefix, string parentPrefix, out string relative)
    {
        relative = string.Empty;
        if (prefix.Length == 0)
        {
            relative = path;
            return path.Length > 0;
        }

        if (!path.Equals(prefix, StringComparison.Ordinal) &&
            !path.StartsWith(prefix + "/", StringComparison.Ordinal))
        {
            return false;
        }

        relative = parentPrefix.Length == 0 ? path : path[(parentPrefix.Length + 1)..];
        return relative.Length > 0;
    }

    private static string ParentOf(string path)
    {
        if (path.Length == 0) return string.Empty;
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    private static string NameOf(string relative)
    {
        var slash = relative.LastIndexOf('/');
        return slash < 0 ? relative : relative[(slash + 1)..];
    }

    /// <summary>Resolves an entry path below the destination and rejects any escape attempt.</summary>
    internal static string ResolveUnder(string root, string relative)
    {
        var normalized = relative.Replace('\\', '/');
        foreach (var part in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "..")
                throw new InvalidDataException("The archive contains an entry that points outside the destination folder.");
        }

        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(rootFull, normalized));
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The archive contains an entry that points outside the destination folder.");
        return full;
    }

    /// <summary>Returns <paramref name="path"/>, or "name (n).ext" when it already exists.</summary>
    internal static string EnsureUniqueFile(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 1; i < 10_000; i++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }

        return Path.Combine(directory, $"{stem} ({Guid.NewGuid():N}){extension}");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record ArchiveItem(string RelativePath, string FullPath, bool IsDirectory);

    private static List<ArchiveItem> CollectItems(IReadOnlyList<string> sourcePaths, CancellationToken ct)
    {
        var items = new List<ArchiveItem>();
        var visitedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sourcePaths)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(source))
                throw new ArgumentException("A source path cannot be empty.", nameof(sourcePaths));

            var full = Path.GetFullPath(source);
            if (Directory.Exists(full))
            {
                AddDirectory(items, visitedDirs, full, Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar)), ct);
            }
            else if (File.Exists(full))
            {
                items.Add(new ArchiveItem(Path.GetFileName(full), full, false));
            }
            else
            {
                throw new FileNotFoundException($"The source '{source}' does not exist.", source);
            }
        }

        return items;
    }

    private static void AddDirectory(
        List<ArchiveItem> items,
        HashSet<string> visitedDirs,
        string fullPath,
        string relativePath,
        CancellationToken ct)
    {
        if (!visitedDirs.Add(fullPath)) return;
        items.Add(new ArchiveItem(relativePath.Replace('\\', '/'), fullPath, true));

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateFileSystemEntries(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var child in children)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(child);
            var childRelative = relativePath + "/" + name;
            if (Directory.Exists(child))
            {
                // Do not follow junctions or symlinked folders: they can form cycles and would
                // otherwise add the same tree several times.
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                AddDirectory(items, visitedDirs, child, childRelative, ct);
            }
            else if (File.Exists(child))
            {
                items.Add(new ArchiveItem(childRelative.Replace('\\', '/'), child, false));
            }
        }
    }

    private static void WriteItems(List<ArchiveItem> items, string target, ArchiveFormat format, CancellationToken ct)
    {
        IWriterOptions options = format switch
        {
            ArchiveFormat.Zip => WriterOptions.ForZip(CompressionType.Deflate),
            ArchiveFormat.Tar => WriterOptions.ForTar(CompressionType.None),
            ArchiveFormat.TarGz => WriterOptions.ForTar(CompressionType.GZip),
            ArchiveFormat.SevenZip => new SevenZipWriterOptions(CompressionType.LZMA2),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown archive format.")
        };

        var archiveType = format switch
        {
            ArchiveFormat.Zip => ArchiveType.Zip,
            ArchiveFormat.Tar or ArchiveFormat.TarGz => ArchiveType.Tar,
            _ => ArchiveType.SevenZip
        };

        using var writer = WriterFactory.OpenWriter(target, archiveType, options);
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var modified = GetLastWriteTime(item.FullPath);

            if (item.IsDirectory)
            {
                writer.WriteDirectory(item.RelativePath, modified);
                continue;
            }

            using var input = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, CopyBufferSize, FileOptions.SequentialScan);
            writer.Write(item.RelativePath, input, modified);
        }
    }

    private static DateTime? GetLastWriteTime(string path)
    {
        try { return File.GetLastWriteTime(path); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

/// <summary>How an archive has to be read: random access or a single forward pass.</summary>
internal enum ArchiveReadMode
{
    /// <summary>Zip, 7z, rar, plain tar: entries can be opened in any order.</summary>
    Direct,

    /// <summary>Compressed tar and single-file compressors: requires a forward pass.</summary>
    Streamed
}

/// <summary>One entry as reported by the archive, already normalised for browsing.</summary>
internal sealed class ArchiveEntryInfo
{
    public string Key { get; init; } = string.Empty;

    /// <summary>Normalised path inside the archive, "" for a single-file compressor.</summary>
    public string Path { get; init; } = string.Empty;

    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTimeOffset Modified { get; init; }
    public bool IsEncrypted { get; init; }
}

internal sealed class ArchiveContents
{
    public ArchiveReadMode Mode { get; init; }
    public IReadOnlyList<ArchiveEntryInfo> Entries { get; init; } = [];
}

/// <summary>
/// Raised when a password is missing or wrong. It derives from <see cref="InvalidOperationException"/>
/// so callers can show a prompt, but it is distinguished from ordinary invalid-operation failures
/// that only mean "this file needs the streaming reader".
/// </summary>
internal sealed class ArchivePasswordException : InvalidOperationException
{
    public ArchivePasswordException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Low-level SharpCompress bridge shared by the archive provider and <see cref="ArchiveService"/>.
/// It hides the two reading strategies and turns SharpCompress failures into the exceptions the
/// UI can act on.
/// </summary>
internal static class ArchiveIo
{
    private const int CopyBufferSize = 128 * 1024;

    internal static string? NormalizePassword(string? password)
        => string.IsNullOrEmpty(password) ? null : password;

    internal static ReaderOptions Options(string? password, bool leaveOpen)
        => new() { Password = password, LeaveStreamOpen = leaveOpen };

    internal static FileStream OpenContainer(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            CopyBufferSize, FileOptions.RandomAccess);

    internal static ArchivePasswordException PasswordRequired(string container)
        => new("ARCHIVE_PASSWORD_REQUIRED",
            new UnauthorizedAccessException($"'{Path.GetFileName(container)}' is encrypted and needs a password."));

    internal static ArchivePasswordException PasswordRejected(string container)
        => new($"The password for '{Path.GetFileName(container)}' is incorrect.",
            new UnauthorizedAccessException("Archive password rejected."));

    /// <summary>Normalises an entry key: forward slashes, no "./", no leading "/", no "..".</summary>
    internal static string NormalizeKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;

        var value = key.Replace('\\', '/').Trim();
        while (value.StartsWith("./", StringComparison.Ordinal)) value = value[2..];
        value = value.TrimStart('/');
        if (value.Length == 0) return string.Empty;

        var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(part);
        }

        return stack.Count == 0 ? string.Empty : string.Join('/', stack);
    }

    /// <summary>Name to show for a single-file compressor (.gz/.bz2/.xz) whose entry has no key.</summary>
    internal static string SingleFileName(string container)
    {
        var name = Path.GetFileName(container);
        var extension = Path.GetExtension(name);
        if (extension.Length == 0 || extension.Length >= name.Length) return name;

        var bare = name[..^extension.Length];
        // A ".tar" inside e.g. "logs.tar.gz" is already the real content name.
        return bare.Length == 0 ? name : bare;
    }

    /// <summary>
    /// Reads the entry table. Tries random access first (zip, 7z, rar, tar) and falls back to a
    /// streaming reader for compressed tars (tar.gz, tar.bz2, tar.xz) and single-file compressors.
    /// </summary>
    internal static ArchiveContents ReadContents(
        string container, string? password, DateTimeOffset fallbackTime, CancellationToken ct)
    {
        var name = Path.GetFileName(container);

        try
        {
            using var fs = OpenContainer(container);
            var options = Options(password, leaveOpen: true);
            using var archive = ArchiveFactory.OpenArchive(fs, options);
            return new ArchiveContents
            {
                Mode = ArchiveReadMode.Direct,
                Entries = ReadDirectEntries(archive, password, container, fallbackTime, ct)
            };
        }
        catch (ArchiveCryptoException)
        {
            throw PasswordError(container, password);
        }
        catch (ArchivePasswordException)
        {
            throw;
        }
        catch (Exception ex) when (ex is SharpCompressException or InvalidOperationException
                                       or EndOfStreamException or ArgumentException)
        {
            // Compressed tars are rejected here ("cannot determine compressed stream type"),
            // so retry through the streaming reader before declaring the file unusable.
        }

        try
        {
            using var fs = OpenContainer(container);
            var options = Options(password, leaveOpen: true);
            using var reader = ReaderFactory.OpenReader(fs, options);
            var list = new List<ArchiveEntryInfo>();
            while (reader.MoveToNextEntry())
            {
                ct.ThrowIfCancellationRequested();
                list.Add(ToEntryInfo(reader.Entry, fallbackTime));
            }

            if (password is null && list.Any(entry => entry.IsEncrypted)) throw PasswordRequired(container);
            return new ArchiveContents { Mode = ArchiveReadMode.Streamed, Entries = list };
        }
        catch (ArchiveCryptoException)
        {
            throw PasswordError(container, password);
        }
        catch (Exception ex) when (ex is SharpCompressException or InvalidOperationException
                                       or EndOfStreamException or ArgumentException or IOException)
        {
            throw new InvalidDataException(
                $"'{name}' is not a supported archive or is damaged.", ex);
        }
    }

    private static List<ArchiveEntryInfo> ReadDirectEntries(
        IArchive archive, string? password, string container, DateTimeOffset fallbackTime,
        CancellationToken ct)
    {
        var list = new List<ArchiveEntryInfo>();
        var encrypted = false;
        IArchiveEntry? probe = null;

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var info = ToEntryInfo(entry, fallbackTime);
            if (info.IsEncrypted)
            {
                encrypted = true;
                probe ??= entry.IsDirectory ? null : entry;
            }
            list.Add(info);
        }

        // Zip headers are readable without the password, so a wrong password would only surface
        // when the user opens the first file. Verify once here so browsing fails fast.
        if (encrypted)
        {
            if (password is null) throw PasswordRequired(container);
            if (probe is not null) VerifyPassword(probe, container);
        }

        return list;
    }

    private static void VerifyPassword(IArchiveEntry entry, string container)
    {
        try
        {
            using var stream = entry.OpenEntryStream();
            stream.ReadByte();
        }
        catch (ArchiveCryptoException)
        {
            throw PasswordRejected(container);
        }
    }

    private static ArchiveEntryInfo ToEntryInfo(IEntry entry, DateTimeOffset fallbackTime)
    {
        var modified = entry.LastModifiedTime is { } time
            ? new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Local))
            : fallbackTime;

        return new ArchiveEntryInfo
        {
            Key = entry.Key ?? string.Empty,
            Path = NormalizeKey(entry.Key),
            IsDirectory = entry.IsDirectory,
            Size = entry.IsDirectory ? 0 : Math.Max(0, entry.Size),
            Modified = modified,
            IsEncrypted = entry.IsEncrypted
        };
    }

    /// <summary>
    /// Streams one entry into <paramref name="destination"/>. The entry stream is only valid
    /// while its archive is open, so the copy happens inside this call.
    /// </summary>
    internal static async Task<long> CopyEntryToAsync(
        string container,
        string? password,
        ArchiveReadMode mode,
        string entryPath,
        Stream destination,
        Action<long>? onBytes,
        CancellationToken ct)
    {
        try
        {
            if (mode == ArchiveReadMode.Direct)
            {
                using var fs = OpenContainer(container);
                using var archive = ArchiveFactory.OpenArchive(fs, Options(password, leaveOpen: true));
                foreach (var entry in archive.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (entry.IsDirectory) continue;
                    var path = NormalizeKey(entry.Key);
                    if (path.Length == 0) path = SingleFileName(container);
                    if (!path.Equals(entryPath, StringComparison.Ordinal)) continue;

                    if (entry.IsEncrypted && password is null) throw PasswordRequired(container);
                    using var source = entry.OpenEntryStream();
                    return await PumpAsync(source, destination, onBytes, ct).ConfigureAwait(false);
                }
            }
            else
            {
                using var fs = OpenContainer(container);
                using var reader = ReaderFactory.OpenReader(fs, Options(password, leaveOpen: true));
                while (reader.MoveToNextEntry())
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = reader.Entry;
                    if (entry.IsDirectory) continue;
                    var path = NormalizeKey(entry.Key);
                    if (path.Length == 0) path = SingleFileName(container);
                    if (!path.Equals(entryPath, StringComparison.Ordinal)) continue;

                    if (entry.IsEncrypted && password is null) throw PasswordRequired(container);
                    using var source = reader.OpenEntryStream();
                    return await PumpAsync(source, destination, onBytes, ct).ConfigureAwait(false);
                }
            }
        }
        catch (ArchiveCryptoException)
        {
            throw PasswordError(container, password);
        }
        catch (ArchivePasswordException)
        {
            throw;
        }
        catch (Exception ex) when (ex is SharpCompressException or InvalidOperationException or EndOfStreamException)
        {
            throw new InvalidDataException(
                $"'{Path.GetFileName(container)}' could not be read: {ex.Message}", ex);
        }

        throw new FileNotFoundException(
            $"'{entryPath}' was not found inside '{Path.GetFileName(container)}'.", entryPath);
    }

    internal static async Task<long> PumpAsync(
        Stream source, Stream destination, Action<long>? onBytes, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        long total = 0;
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                total += read;
                onBytes?.Invoke(total);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return total;
    }

    private static ArchivePasswordException PasswordError(string container, string? password)
        => password is null ? PasswordRequired(container) : PasswordRejected(container);
}
