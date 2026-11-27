// Copyright (c) 2026 Hub-code-develop. Licensed under the MIT License.
//
// SFTP location on top of SSH.NET. One SftpClient is reused across calls because a handshake
// costs a full key exchange; the client is not thread safe, so every operation is serialized
// through a semaphore and the session is dropped after a minute of inactivity.
//
// Known trade-off: HostKeyReceived accepts every host key. A NAS reached by IP, or a machine that
// was reinstalled, would otherwise be unusable without a fingerprint prompt this library cannot
// provide. The transport is still encrypted, but it is not authenticated; a future version should
// pin the fingerprint per saved location.
//
// Reads and writes are staged through the local temp folder. That keeps every operation short
// (so the shared session is never held open by a caller reading a large file) at the cost of one
// extra copy on disk.

using System.Security.Cryptography;
using FileHub.Files.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace FileHub.Files.Vfs;

public sealed class SftpFileSystemProvider : IFileSystemProvider, IDisposable
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    private readonly RemoteLocation _location;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _basePath;
    private readonly Timer _idleTimer;

    private SftpClient? _client;
    private DateTimeOffset _lastUsed = DateTimeOffset.UtcNow;
    private bool _disposed;

    /// <summary>Creates a provider for one saved SFTP bookmark. The session opens lazily.</summary>
    /// <exception cref="InvalidOperationException">Host, user or password is missing.</exception>
    public SftpFileSystemProvider(RemoteLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (string.IsNullOrWhiteSpace(location.Host))
            throw new InvalidOperationException("An SFTP location needs a host name");
        if (string.IsNullOrWhiteSpace(location.User))
            throw new InvalidOperationException("An SFTP location needs a user name");
        if (string.IsNullOrEmpty(location.Password))
            throw new InvalidOperationException(
                $"No password is available for '{location.Name}'. Re-enter the credentials for this SFTP location.");

        _location = location;
        var prefix = location.ToRelativePath("/");
        _basePath = prefix.Length == 0 ? string.Empty : "/" + prefix;
        _idleTimer = new Timer(_ => ReleaseIfIdle(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    /// <summary>The bookmark this provider serves.</summary>
    public RemoteLocation Location => _location;

    /// <inheritdoc/>
    public VfsScheme Scheme => VfsScheme.Sftp;

    /// <inheritdoc/>
    public string DisplayName => "SFTP";

    public async Task<IReadOnlyList<FileEntry>> ListAsync(VfsPath path, bool includeHidden, CancellationToken cancellationToken)
    {
        var remotePath = ToRemotePath(path);
        var files = await WithClientAsync(
            client => client.ListDirectory(remotePath, _ => { }).ToList(), cancellationToken).ConfigureAwait(false);

        var result = new List<FileEntry>(files.Count);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // SSH.NET reports the two directory links as regular entries.
            if (file.Name is "." or "..") continue;

            var isHidden = file.Name.StartsWith('.');
            if (isHidden && !includeHidden) continue;

            var virtualPath = path.Combine(file.Name);

            result.Add(new FileEntry
            {
                Name = file.Name,
                FullPath = virtualPath.Canonical,
                IsDirectory = file.IsDirectory,
                IsHidden = isHidden,
                Size = file.IsDirectory ? 0 : file.Length,
                LastWriteTime = ToOffset(file.LastWriteTimeUtc)
            });
        }

        return result;
    }

    public async Task<VfsStat> StatAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var remotePath = ToRemotePath(path);
        try
        {
            var attributes = await WithClientAsync(client => client.GetAttributes(remotePath), cancellationToken)
                .ConfigureAwait(false);
            return new VfsStat(true, attributes.IsDirectory, attributes.IsDirectory ? 0 : attributes.Size,
                ToOffset(attributes.LastWriteTimeUtc));
        }
        catch (SftpPathNotFoundException)
        {
            return VfsStat.Missing;
        }
    }

    public async Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var remotePath = ToRemotePath(path);
        var tempFile = Path.Combine(Path.GetTempPath(), "filehub-sftp-" + Guid.NewGuid().ToString("n") + ".tmp");

        try
        {
            await WithClientAsync(client =>
            {
                using var target = new FileStream(tempFile, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    64 * 1024, FileOptions.None);
                client.DownloadFile(remotePath, target);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(tempFile);
            throw;
        }

        // DeleteOnClose keeps the temp copy from outliving the stream even if the caller crashes.
        return new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
    }

    public Task<Stream> CreateFileAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var remotePath = ToRemotePath(path);
        return Task.FromResult<Stream>(new RemoteStagedWriteStream(async (tempFile, ct) =>
        {
            await WithClientAsync(client =>
            {
                using var source = new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.Read,
                    64 * 1024, FileOptions.SequentialScan);
                client.UploadFile(source, remotePath, canOverride: true);
                return true;
            }, ct).ConfigureAwait(false);
        }, cancellationToken));
    }

    public async Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var remotePath = ToRemotePath(path);
        await WithClientAsync(client =>
        {
            if (!client.Exists(remotePath)) client.CreateDirectory(remotePath);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken)
    {
        var remotePath = ToRemotePath(path);
        await WithClientAsync(client =>
        {
            DeleteCore(client, remotePath, recursive);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static void DeleteCore(SftpClient client, string remotePath, bool recursive)
    {
        var attributes = client.GetAttributes(remotePath);
        if (!attributes.IsDirectory)
        {
            client.DeleteFile(remotePath);
            return;
        }

        var children = client.ListDirectory(remotePath, _ => { })
            .Where(file => file.Name is not ("." or ".."))
            .ToList();

        if (children.Count > 0 && !recursive)
            throw new IOException($"The folder '{remotePath}' is not empty");

        foreach (var child in children)
            DeleteCore(client, child.FullName, recursive: true);

        client.DeleteDirectory(remotePath);
    }

    public async Task RenameAsync(VfsPath path, string newName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName.Contains('/') || newName.Contains('\\'))
            throw new ArgumentException("The new name must be a single path segment", nameof(newName));

        var parent = path.Parent ?? throw new IOException("Cannot rename the root of an SFTP location");
        await MoveAsync(path, parent.Combine(newName), cancellationToken).ConfigureAwait(false);
    }

    public async Task MoveAsync(VfsPath source, VfsPath target, CancellationToken cancellationToken)
    {
        var from = ToRemotePath(source);
        var to = ToRemotePath(target);
        if (string.Equals(from, to, StringComparison.Ordinal))
            throw new IOException("The source and target are the same item");

        await WithClientAsync(client =>
        {
            if (client.Exists(to))
                throw new IOException($"An item named '{target.FileName}' already exists");
            client.RenameFile(from, to, isPosix: true);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> WithClientAsync<T>(Func<SftpClient, T> operation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = EnsureConnected();
            var result = await Task.Run(() => operation(client), cancellationToken).ConfigureAwait(false);
            _lastUsed = DateTimeOffset.UtcNow;
            return result;
        }
        catch (SftpPathNotFoundException ex)
        {
            throw new FileNotFoundException($"SFTP path not found: {ex.Message}", ex.Message, ex);
        }
        catch (SftpPermissionDeniedException ex)
        {
            throw new UnauthorizedAccessException($"SFTP permission denied: {ex.Message}", ex);
        }
        catch (SshAuthenticationException ex)
        {
            Disconnect();
            throw new UnauthorizedAccessException(
                $"SFTP authentication failed for '{_location.User}@{_location.Host}': {ex.Message}", ex);
        }
        catch (SshException ex)
        {
            // A dropped or half-open session must not poison every later call.
            Disconnect();
            throw new IOException($"SFTP error on {_location.Host}: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException)
        {
            Disconnect();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private SftpClient EnsureConnected()
    {
        if (_client is { IsConnected: true }) return _client;
        Disconnect();

        var user = _location.User!;
        var connectionInfo = new ConnectionInfo(
            _location.Host,
            _location.ResolvePort(),
            user,
            new PasswordAuthenticationMethod(user, _location.Password!));

        var client = new SftpClient(connectionInfo)
        {
            OperationTimeout = TimeSpan.FromSeconds(30),
            KeepAliveInterval = TimeSpan.FromSeconds(20)
        };

        // See the file header: accepting any host key is a deliberate, documented trade-off.
        client.HostKeyReceived += (_, args) => args.CanTrust = true;

        client.Connect();
        _client = client;
        _lastUsed = DateTimeOffset.UtcNow;
        return client;
    }

    private void ReleaseIfIdle()
    {
        if (_disposed) return;
        if (DateTimeOffset.UtcNow - _lastUsed < IdleTimeout) return;
        if (!_gate.Wait(0)) return;
        try
        {
            if (DateTimeOffset.UtcNow - _lastUsed >= IdleTimeout) Disconnect();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Disconnect()
    {
        var client = _client;
        _client = null;
        if (client is null) return;
        try
        {
            if (client.IsConnected) client.Disconnect();
        }
        catch (SshException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            client.Dispose();
        }
    }

    private string ToRemotePath(VfsPath path)
    {
        var inner = path.Inner.Length == 0 ? "/" : path.Inner;
        if (!inner.StartsWith('/')) inner = "/" + inner;
        return _basePath.Length == 0 ? inner : _basePath + inner;
    }

    private static DateTimeOffset ToOffset(DateTime value)
        => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Stops the idle timer and closes the session.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _idleTimer.Dispose();
        _gate.Wait();
        try
        {
            Disconnect();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
