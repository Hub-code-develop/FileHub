// Copyright (c) 2026 Hub-code-develop. Licensed under the MIT License.
//
// Provider contract for every location FileHub can browse: the local disk, an archive, a NAS
// share, an SFTP server, a WebDAV endpoint or an S3 bucket. The desktop layer only ever talks
// to this interface, so navigation, search and file operations work identically everywhere.

using FileHub.Files.Models;

namespace FileHub.Files.Vfs;

public readonly record struct VfsStat(bool Exists, bool IsDirectory, long Size, DateTimeOffset Modified)
{
    public static VfsStat Missing => new(false, false, 0, default);
}

/// <summary>What to do when a transfer target already exists.</summary>
public enum VfsConflictPolicy
{
    Rename,
    Overwrite,
    Skip
}

public enum VfsConflictAnswer
{
    Skip,
    Overwrite,
    KeepBoth,
    Cancel
}

public sealed record VfsTransferProgress(
    string ItemName, long BytesDone, long TotalBytes, int ItemIndex, int ItemCount)
{
    public double Fraction => TotalBytes <= 0 ? 0 : Math.Clamp((double)BytesDone / TotalBytes, 0, 1);
}

public interface IFileSystemProvider
{
    VfsScheme Scheme { get; }

    /// <summary>Shown in the sidebar location group, e.g. "Network share".</summary>
    string DisplayName { get; }

    Task<IReadOnlyList<FileEntry>> ListAsync(VfsPath path, bool includeHidden, CancellationToken cancellationToken);

    Task<VfsStat> StatAsync(VfsPath path, CancellationToken cancellationToken);

    Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken);

    /// <summary>Opens a target for writing; the caller disposes it. Not every provider is writable.</summary>
    Task<Stream> CreateFileAsync(VfsPath path, CancellationToken cancellationToken)
        => throw new NotSupportedException($"{DisplayName} is read-only");

    Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken)
        => throw new NotSupportedException($"{DisplayName} is read-only");

    Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken)
        => throw new NotSupportedException($"{DisplayName} is read-only");

    Task RenameAsync(VfsPath path, string newName, CancellationToken cancellationToken)
        => throw new NotSupportedException($"{DisplayName} is read-only");

    /// <summary>
    /// Moves an entry to a new parent. Providers that can do this server-side override it; the
    /// default falls back to copy-then-delete in <see cref="VfsFileSystem"/>.
    /// </summary>
    Task MoveAsync(VfsPath source, VfsPath target, CancellationToken cancellationToken)
        => throw new NotSupportedException($"{DisplayName} cannot move items directly");

    /// <summary>Warms any cached snapshot so a following ListAsync is instant.</summary>
    Task PrimeAsync(VfsPath path, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Process-wide provider table. Plugins may register additional schemes at runtime.</summary>
public static class VfsRegistry
{
    private static readonly Dictionary<VfsScheme, IFileSystemProvider> Table = [];
    private static readonly Lock Gate = new();

    public static void Register(IFileSystemProvider provider)
    {
        lock (Gate) Table[provider.Scheme] = provider;
    }

    public static bool TryResolve(VfsScheme scheme, out IFileSystemProvider provider)
    {
        lock (Gate) return Table.TryGetValue(scheme, out provider!);
    }

    public static IFileSystemProvider Resolve(VfsPath path)
    {
        if (path.IsLocal) throw new InvalidOperationException("Local paths never go through a provider");
        if (TryResolve(path.Scheme, out var provider)) return provider;
        throw new NotSupportedException($"No provider is registered for '{VfsPath.Token(path.Scheme)}'");
    }

    public static IReadOnlyList<IFileSystemProvider> All
    {
        get { lock (Gate) return Table.Values.ToList(); }
    }
}
