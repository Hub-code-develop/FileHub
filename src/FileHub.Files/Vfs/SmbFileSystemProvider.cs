// Copyright (c) 2026 Hub-code-develop. Licensed under the MIT License.
//
// SMB location for NAS shares, driven by SMBLibrary (pure managed SMB2/3, no native mount).
//
// Deliberately conservative: every operation opens its own session, does one thing and closes.
// A pooled session with idle expiry would be faster, but SMB2 handles, oplocks and session
// timeouts make that easy to get subtly wrong, and a browse action is rare enough that one extra
// logon is acceptable. Where the underlying call allows it (reads), the handle is kept open for
// the lifetime of the returned stream and the session dies with it.
//
// Credentials: "DOMAIN\user" is split into domain and user; "user" logs on to the local machine.
// NTLMv2 is negotiated by SMBLibrary during SessionSetup.

using System.Net;
using FileHub.Files.Models;
using SMBLibrary;
using SmbFileAttributes = SMBLibrary.FileAttributes;
using SMBLibrary.Client;

namespace FileHub.Files.Vfs;

public sealed class SmbFileSystemProvider : IFileSystemProvider
{
    private const int ResponseTimeoutMs = 60_000;
    private const int WriteChunkSize = 1 * 1024 * 1024;

    private readonly RemoteLocation _location;
    private readonly string _share;

    /// <summary>Creates a provider for one saved SMB bookmark.</summary>
    /// <exception cref="InvalidOperationException">Host or share name is missing, or the port is not 139/445.</exception>
    public SmbFileSystemProvider(RemoteLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (string.IsNullOrWhiteSpace(location.Host))
            throw new InvalidOperationException("An SMB location needs a host name");
        if (string.IsNullOrWhiteSpace(location.ShareName))
            throw new InvalidOperationException(
                "An SMB location needs a share name. Put it in BasePath, e.g. 'share' or 'share/folder'");

        var port = location.ResolvePort();
        if (port is not (139 or 445))
            throw new InvalidOperationException(
                $"SMB supports port 445 (direct) or 139 (NetBIOS) only; '{location.Host}:{port}' cannot be opened");

        _location = location;
        _share = location.ShareName;
    }

    /// <summary>The bookmark this provider serves.</summary>
    public RemoteLocation Location => _location;

    /// <inheritdoc/>
    public VfsScheme Scheme => VfsScheme.Smb;

    /// <inheritdoc/>
    public string DisplayName => "SMB share";

    public Task<IReadOnlyList<FileEntry>> ListAsync(VfsPath path, bool includeHidden, CancellationToken cancellationToken)
    {
        return RunAsync(session =>
        {
            var smbPath = ToSmbPath(path);
            var entries = new List<FileEntry>();

            var status = session.Store.CreateFile(out var handle, out _, smbPath, AccessMask.GENERIC_READ,
                SmbFileAttributes.Directory, ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
                CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null);
            EnsureSuccess(status, "open directory", smbPath);

            try
            {
                while (true)
                {
                    status = session.Store.QueryDirectory(out var batch, handle, "*",
                        FileInformationClass.FileDirectoryInformation);

                    if (status == NTStatus.STATUS_NO_MORE_FILES) break;
                    EnsureSuccess(status, "list directory", smbPath);

                    foreach (var item in batch)
                    {
                        if (item is not FileDirectoryInformation info) continue;
                        if (info.FileName is "." or "..") continue;

                        var isDirectory = (info.FileAttributes & SmbFileAttributes.Directory) != 0;
                        var isHidden = info.FileName.StartsWith('.') ||
                                       (info.FileAttributes & SmbFileAttributes.Hidden) != 0;
                        if (isHidden && !includeHidden) continue;

                        entries.Add(new FileEntry
                        {
                            Name = info.FileName,
                            FullPath = path.Combine(info.FileName).Canonical,
                            IsDirectory = isDirectory,
                            IsHidden = isHidden,
                            Size = isDirectory ? 0 : info.EndOfFile,
                            LastWriteTime = ToOffset(info.LastWriteTime)
                        });
                    }

                    if (status != NTStatus.STATUS_SUCCESS) break;
                }
            }
            finally
            {
                session.Store.CloseFile(handle);
            }

            return (IReadOnlyList<FileEntry>)entries;
        }, cancellationToken);
    }

    public Task<VfsStat> StatAsync(VfsPath path, CancellationToken cancellationToken)
    {
        return RunAsync(session =>
        {
            var smbPath = ToSmbPath(path);
            var status = session.Store.CreateFile(out var handle, out _, smbPath,
                AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, SmbFileAttributes.Normal,
                ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
                CreateDisposition.FILE_OPEN, (CreateOptions)0, null);

            if (IsNotFound(status)) return VfsStat.Missing;
            EnsureSuccess(status, "stat", smbPath);

            try
            {
                status = session.Store.GetFileInformation(out var information, handle,
                    FileInformationClass.FileAllInformation);
                EnsureSuccess(status, "stat", smbPath);
                if (information is not FileAllInformation all) return VfsStat.Missing;

                var directory = all.StandardInformation.Directory;
                var modified = (DateTime?)all.BasicInformation.LastWriteTime;
                return new VfsStat(true, directory, directory ? 0 : all.StandardInformation.EndOfFile,
                    modified.HasValue ? ToOffset(modified.Value) : default);
            }
            finally
            {
                session.Store.CloseFile(handle);
            }
        }, cancellationToken);
    }

    public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var session = Connect();
        try
        {
            var smbPath = ToSmbPath(path);
            var status = session.Store.CreateFile(out var handle, out _, smbPath,
                AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, SmbFileAttributes.Normal,
                ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
                CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE, null);
            if (status != NTStatus.STATUS_SUCCESS)
            {
                session.Dispose();
                EnsureSuccess(status, "open file", smbPath);
            }

            status = session.Store.GetFileInformation(out var information, handle,
                FileInformationClass.FileAllInformation);
            var length = status == NTStatus.STATUS_SUCCESS && information is FileAllInformation all
                ? all.StandardInformation.EndOfFile
                : 0;

            return Task.FromResult<Stream>(new SmbReadStream(session, handle, length));
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public Task<Stream> CreateFileAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var smbPath = ToSmbPath(path);
        return Task.FromResult<Stream>(new RemoteStagedWriteStream(async (tempFile, ct) =>
        {
            var session = Connect();
            try
            {
                var status = session.Store.CreateFile(out var handle, out _, smbPath,
                    AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, SmbFileAttributes.Normal,
                    ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OVERWRITE_IF,
                    CreateOptions.FILE_NON_DIRECTORY_FILE, null);
                EnsureSuccess(status, "create file", smbPath);

                try
                {
                    var chunkSize = (int)Math.Min(WriteChunkSize, Math.Max(64 * 1024u, session.Store.MaxWriteSize));
                    var buffer = new byte[chunkSize];
                    await using var source = new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.Read,
                        64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

                    long offset = 0;
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
                        if (read == 0) break;

                        var payload = read == buffer.Length ? buffer : buffer[..read];
                        status = session.Store.WriteFile(out var written, handle, offset, payload);
                        EnsureSuccess(status, "write file", smbPath);
                        if (written != read)
                            throw new IOException($"SMB wrote only {written} of {read} bytes to {smbPath}");
                        offset += read;
                    }
                }
                finally
                {
                    session.Store.CloseFile(handle);
                }
            }
            finally
            {
                session.Dispose();
            }
        }, cancellationToken));
    }

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        return RunAsync(session =>
        {
            var smbPath = ToSmbPath(path);
            var status = session.Store.CreateFile(out var handle, out _, smbPath,
                AccessMask.GENERIC_WRITE, SmbFileAttributes.Directory,
                ShareAccess.Read | ShareAccess.Write,
                CreateDisposition.FILE_CREATE, CreateOptions.FILE_DIRECTORY_FILE, null);

            if (status == NTStatus.STATUS_OBJECT_NAME_COLLISION) return true;
            EnsureSuccess(status, "create directory", smbPath);
            session.Store.CloseFile(handle);
            return true;
        }, cancellationToken);
    }

    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken)
    {
        return RunAsync(session =>
        {
            DeleteCore(session, ToSmbPath(path), recursive);
            return true;
        }, cancellationToken);
    }

    private void DeleteCore(SmbSession session, string smbPath, bool recursive)
    {
        var status = session.Store.CreateFile(out var probe, out _, smbPath,
            AccessMask.GENERIC_READ, SmbFileAttributes.Normal,
            ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
            CreateDisposition.FILE_OPEN, (CreateOptions)0, null);
        EnsureSuccess(status, "delete", smbPath);

        var isDirectory = false;
        try
        {
            status = session.Store.GetFileInformation(out var information, probe, FileInformationClass.FileAllInformation);
            if (status == NTStatus.STATUS_SUCCESS && information is FileAllInformation all)
                isDirectory = all.StandardInformation.Directory;
        }
        finally
        {
            session.Store.CloseFile(probe);
        }

        if (isDirectory)
        {
            status = session.Store.CreateFile(out var listing, out _, smbPath, AccessMask.GENERIC_READ,
                SmbFileAttributes.Directory, ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
                CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null);
            EnsureSuccess(status, "delete", smbPath);

            var children = new List<string>();
            try
            {
                while (true)
                {
                    status = session.Store.QueryDirectory(out var batch, listing, "*",
                        FileInformationClass.FileDirectoryInformation);
                    if (status == NTStatus.STATUS_NO_MORE_FILES) break;
                    EnsureSuccess(status, "delete", smbPath);

                    foreach (var item in batch)
                    {
                        if (item is not FileDirectoryInformation info) continue;
                        if (info.FileName is "." or "..") continue;
                        children.Add(info.FileName);
                    }
                }
            }
            finally
            {
                session.Store.CloseFile(listing);
            }

            if (children.Count > 0 && !recursive)
                throw new IOException($"The folder '{smbPath}' is not empty");

            foreach (var child in children)
                DeleteCore(session, CombineSmb(smbPath, child), recursive: true);
        }

        // FILE_DELETE_ON_CLOSE removes the entry when the last handle closes; this is the one-step
        // delete SMB2 offers and it works for both files and empty directories.
        var options = isDirectory ? CreateOptions.FILE_DELETE_ON_CLOSE | CreateOptions.FILE_DIRECTORY_FILE
                                  : CreateOptions.FILE_DELETE_ON_CLOSE | CreateOptions.FILE_NON_DIRECTORY_FILE;
        status = session.Store.CreateFile(out var handle, out _, smbPath, AccessMask.DELETE,
            isDirectory ? SmbFileAttributes.Directory : SmbFileAttributes.Normal,
            ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
            CreateDisposition.FILE_OPEN, options, null);
        EnsureSuccess(status, "delete", smbPath);
        session.Store.CloseFile(handle);
    }

    public async Task RenameAsync(VfsPath path, string newName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName.Contains('/') || newName.Contains('\\'))
            throw new ArgumentException("The new name must be a single path segment", nameof(newName));

        var parent = path.Parent ?? throw new IOException("Cannot rename the share root");
        await MoveAsync(path, parent.Combine(newName), cancellationToken).ConfigureAwait(false);
    }

    public Task MoveAsync(VfsPath source, VfsPath target, CancellationToken cancellationToken)
    {
        return RunAsync(session =>
        {
            var from = ToSmbPath(source);
            var to = ToSmbPath(target);
            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The source and target are the same item");

            var status = session.Store.CreateFile(out var handle, out _, from, AccessMask.DELETE,
                SmbFileAttributes.Normal, ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
                CreateDisposition.FILE_OPEN, (CreateOptions)0, null);
            EnsureSuccess(status, "move", from);

            try
            {
                var rename = new FileRenameInformationType2
                {
                    ReplaceIfExists = false,
                    RootDirectory = 0,
                    FileName = to
                };
                status = session.Store.SetFileInformation(handle, rename);
                EnsureSuccess(status, "move", from);
            }
            finally
            {
                session.Store.CloseFile(handle);
            }

            return true;
        }, cancellationToken);
    }

    private async Task<T> RunAsync<T>(Func<SmbSession, T> operation, CancellationToken cancellationToken)
    {
        var session = Connect();
        try
        {
            return await Task.Run(() => operation(session), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            session.Dispose();
        }
    }

    private SmbSession Connect()
    {
        // SMBLibrary hard-codes the two well-known ports (445 direct, 139 over NetBIOS); there is
        // no API to pass a port, so a location on a non-standard port is rejected up front rather
        // than failing with a confusing timeout.
        var port = _location.ResolvePort();
        var transport = port == 139 ? SMBTransportType.NetBiosOverTCP : SMBTransportType.DirectTCPTransport;

        var client = new SMB2Client(ResponseTimeoutMs, enableSMB311Support: true);

        if (!client.Connect(_location.Host, transport))
            throw new IOException($"SMB could not connect to {_location.Host}:{port}");

        var (domain, user) = SplitUser(_location.User);
        var status = client.Login(domain, user, _location.Password ?? string.Empty);
        if (status != NTStatus.STATUS_SUCCESS)
        {
            try { client.Disconnect(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            throw ToException(status, "logon", $"{_location.Host}\\{_share}");
        }

        var store = client.TreeConnect(_share, out status);
        if (status != NTStatus.STATUS_SUCCESS || store is null)
        {
            try { client.Logoff(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            try { client.Disconnect(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            throw ToException(status, "connect to share", $"{_location.Host}\\{_share}");
        }

        return new SmbSession(client, store);
    }

    private string ToSmbPath(VfsPath path)
    {
        // ToRelativePath already drops the share segment for SMB: the share lives in the authority.
        var relative = _location.ToRelativePath(path.Inner);
        if (relative.Length == 0) return "\\";
        return "\\" + relative.Replace('/', '\\');
    }

    private static string CombineSmb(string parent, string child)
        => parent.EndsWith('\\') ? parent + child : parent + "\\" + child;

    private static (string Domain, string User) SplitUser(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (string.Empty, string.Empty);
        var value = raw.Trim();
        var slash = value.IndexOf('\\');
        return slash > 0 ? (value[..slash], value[(slash + 1)..]) : (string.Empty, value);
    }

    private static bool IsNotFound(NTStatus status) => status is
        NTStatus.STATUS_OBJECT_NAME_NOT_FOUND or
        NTStatus.STATUS_OBJECT_PATH_NOT_FOUND or
        NTStatus.STATUS_NO_SUCH_FILE or
        NTStatus.STATUS_NOT_FOUND;

    private static void EnsureSuccess(NTStatus status, string operation, string path)
    {
        if (status is NTStatus.STATUS_SUCCESS or NTStatus.STATUS_PENDING) return;
        throw ToException(status, operation, path);
    }

    private static Exception ToException(NTStatus status, string operation, string path)
    {
        var label = $"SMB {status} on {operation}";
        return status switch
        {
            NTStatus.STATUS_LOGON_FAILURE or
            NTStatus.STATUS_WRONG_PASSWORD or
            NTStatus.STATUS_ACCOUNT_LOCKED_OUT or
            NTStatus.STATUS_ACCOUNT_DISABLED or
            NTStatus.STATUS_ACCOUNT_EXPIRED or
            NTStatus.STATUS_PASSWORD_EXPIRED => new UnauthorizedAccessException(
                $"SMB 401: credentials rejected for {path} ({status})"),

            NTStatus.STATUS_ACCESS_DENIED => new UnauthorizedAccessException(
                $"SMB 403: access denied for {path} ({status})"),

            NTStatus.STATUS_OBJECT_NAME_NOT_FOUND or
            NTStatus.STATUS_OBJECT_PATH_NOT_FOUND or
            NTStatus.STATUS_NO_SUCH_FILE or
            NTStatus.STATUS_NOT_FOUND => new FileNotFoundException(
                $"SMB 404: not found: {path}", path),

            NTStatus.STATUS_BAD_NETWORK_NAME => new IOException(
                $"SMB share not found: {path} ({status}). Check the share name and that it exists."),

            NTStatus.STATUS_OBJECT_NAME_COLLISION => new IOException(
                $"SMB: an item named '{path}' already exists"),

            NTStatus.STATUS_DIRECTORY_NOT_EMPTY => new IOException($"SMB: the folder '{path}' is not empty"),
            NTStatus.STATUS_DISK_FULL => new IOException($"SMB: not enough space on the server ({path})"),
            NTStatus.STATUS_FILE_IS_A_DIRECTORY => new IOException($"SMB: '{path}' is a directory"),
            NTStatus.STATUS_NOT_A_DIRECTORY => new IOException($"SMB: '{path}' is not a directory"),
            NTStatus.STATUS_SHARING_VIOLATION => new IOException(
                $"SMB: '{path}' is in use by another program ({status})"),

            _ => new IOException($"{label}: {status} ({path})")
        };
    }

    private static DateTimeOffset ToOffset(DateTime value)
        => value.Kind == DateTimeKind.Local
            ? new DateTimeOffset(value)
            : new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    /// <summary>One connected session: client plus the tree connection to the share.</summary>
    private sealed class SmbSession : IDisposable
    {
        private readonly SMB2Client _client;
        private bool _disposed;

        public SmbSession(SMB2Client client, ISMBFileStore store)
        {
            _client = client;
            Store = store;
        }

        public ISMBFileStore Store { get; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Store.Disconnect(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            try { _client.Logoff(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            try { _client.Disconnect(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
    }

    /// <summary>Read stream over one open SMB2 handle; closing it releases the session.</summary>
    private sealed class SmbReadStream : Stream
    {
        private readonly SmbSession _session;
        private readonly object _handle;
        private readonly long _length;
        private long _position;
        private bool _disposed;

        public SmbReadStream(SmbSession session, object handle, long length)
        {
            _session = session;
            _handle = handle;
            _length = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0) return 0;
            if (_position >= _length) return 0;

            var remaining = _length - _position;
            var request = (int)Math.Min(count, remaining);
            var status = _session.Store.ReadFile(out var data, _handle, _position, request);
            if (status == NTStatus.STATUS_END_OF_FILE) return 0;
            EnsureSuccess(status, "read", _handle.ToString() ?? string.Empty);

            var available = Math.Min(data.Length, request);
            if (available == 0) return 0;
            Buffer.BlockCopy(data, 0, buffer, offset, available);
            _position += available;
            return available;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                var read = Read(rented, 0, buffer.Length);
                rented.AsSpan(0, read).CopyTo(buffer.Span);
                return ValueTask.FromResult(read);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => _length + offset
            };
            return _position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!disposing || _disposed) return;
            _disposed = true;
            try { _session.Store.CloseFile(_handle); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            _session.Dispose();
            base.Dispose(disposing);
        }
    }
}
