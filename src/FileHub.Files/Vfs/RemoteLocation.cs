// Copyright (c) 2026 Hub-code-develop. Licensed under the MIT License.
//
// A remote location is one saved bookmark: which protocol, which host, which credentials.
// Providers (WebDAV/SFTP/SMB/S3) take one of these and expose the same IFileSystemProvider
// surface the local disk has, so the browser never learns the difference.
//
// Secrets are never written to the JSON file in clear text. Each password is handed to the
// best vault the OS offers and only an opaque token is persisted:
//   dpapi:<base64>        Windows DPAPI blob tied to the current user (crypt32, no extra package)
//   keychain:<account>    macOS login keychain via /usr/bin/security
//   secret-tool:<account> Linux Secret Service via secret-tool
// When no vault is usable the token is "missing:" and the password is deliberately dropped;
// the user is asked again next time instead of us writing something that only looks encrypted.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileHub.Files.Vfs;

public enum RemoteProtocol
{
    WebDav,
    Sftp,
    Smb,
    S3
}

/// <summary>
/// One saved remote bookmark. <see cref="Host"/> holds the server name for WebDAV/SFTP/SMB and
/// the bucket name for S3; <see cref="BasePath"/> is the folder the bookmark is rooted at
/// (the share name for SMB, the key prefix for S3).
/// </summary>
public sealed class RemoteLocation
{
    /// <summary>Stable identifier used as the keychain account name.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    /// <summary>Display name shown in the sidebar.</summary>
    public string Name { get; set; } = string.Empty;

    public RemoteProtocol Protocol { get; set; }

    /// <summary>Server host name, or the bucket name for S3.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>TCP port; 0 means "use the protocol default".</summary>
    public int Port { get; set; }

    /// <summary>User name; the access key id for S3.</summary>
    public string? User { get; set; }

    /// <summary>Password (pulled from the OS vault on load); the secret key for S3.</summary>
    public string? Password { get; set; }

    /// <summary>Root folder inside the server: share name for SMB, key prefix for S3.</summary>
    public string? BasePath { get; set; }

    /// <summary>Use HTTPS for WebDAV.</summary>
    public bool UseTls { get; set; }

    /// <summary>S3 region; defaults to us-east-1.</summary>
    public string? Region { get; set; }

    /// <summary>S3 endpoint override for MinIO or another compatible server, e.g. http://nas:9000.</summary>
    public string? Endpoint { get; set; }

    /// <summary>S3 secret key; falls back to <see cref="Password"/> when null.</summary>
    public string? SecretKey { get; set; }

    /// <summary>Scheme this location browses as.</summary>
    [JsonIgnore]
    public VfsScheme Scheme => Protocol switch
    {
        RemoteProtocol.WebDav => VfsScheme.WebDav,
        RemoteProtocol.Sftp => VfsScheme.Sftp,
        RemoteProtocol.Smb => VfsScheme.Smb,
        _ => VfsScheme.S3
    };

    /// <summary>Default port for the protocol, honouring <see cref="UseTls"/> for WebDAV.</summary>
    [JsonIgnore]
    public int DefaultPort => Protocol switch
    {
        RemoteProtocol.WebDav => UseTls ? 443 : 80,
        RemoteProtocol.Sftp => 22,
        RemoteProtocol.Smb => 445,
        _ => 443
    };

    /// <summary>Configured port, or the protocol default.</summary>
    public int ResolvePort() => Port > 0 ? Port : DefaultPort;

    /// <summary>First segment of <see cref="BasePath"/>; the share name for SMB.</summary>
    [JsonIgnore]
    public string ShareName
    {
        get
        {
            var path = (BasePath ?? string.Empty).Replace('\\', '/').Trim('/');
            var slash = path.IndexOf('/');
            return slash < 0 ? path : path[..slash];
        }
    }

    /// <summary>Remainder of <see cref="BasePath"/> after the share name; empty at the share root.</summary>
    [JsonIgnore]
    public string ShareSubPath
    {
        get
        {
            var path = (BasePath ?? string.Empty).Replace('\\', '/').Trim('/');
            var slash = path.IndexOf('/');
            return slash < 0 ? string.Empty : path[(slash + 1)..];
        }
    }

    /// <summary>Authority half of the canonical virtual path (smb uses host/share).</summary>
    [JsonIgnore]
    public string Authority
    {
        get
        {
            switch (Protocol)
            {
                case RemoteProtocol.Smb:
                    var share = ShareName;
                    return share.Length == 0 ? Host : $"{Host}/{share}";
                case RemoteProtocol.Sftp:
                    return string.IsNullOrEmpty(User) ? Host : $"{User}@{Host}";
                case RemoteProtocol.WebDav:
                    return Host;
                default:
                    return Host;
            }
        }
    }

    /// <summary>Canonical virtual path of this bookmark's root, e.g. sftp://me@host/.</summary>
    [JsonIgnore]
    public string RootPath => new VfsPath(Scheme, Authority, "/").Canonical;

    /// <summary>
    /// Maps a virtual inner path onto a server-relative path (no leading slash). The mount
    /// prefix is prepended for every protocol; SMB additionally drops its share segment because
    /// the share is already part of the authority.
    /// </summary>
    public string ToRelativePath(string inner)
    {
        var trimmed = (inner ?? "/").Trim('/');
        var basePath = Protocol == RemoteProtocol.Smb ? ShareSubPath : (BasePath ?? string.Empty).Replace('\\', '/').Trim('/');
        if (basePath.Length == 0) return trimmed;
        return trimmed.Length == 0 ? basePath : basePath + "/" + trimmed;
    }

    /// <summary>Builds the provider for this location.</summary>
    public IFileSystemProvider CreateProvider() => Protocol switch
    {
        RemoteProtocol.WebDav => new WebDavFileSystemProvider(this),
        RemoteProtocol.Sftp => new SftpFileSystemProvider(this),
        RemoteProtocol.Smb => new SmbFileSystemProvider(this),
        _ => new S3FileSystemProvider(this)
    };
}

/// <summary>Loads and saves <see cref="RemoteLocation"/> bookmarks under the roaming app data folder.</summary>
public static class RemoteLocationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Full path of the JSON file, %APPDATA%/FileHub/remote-locations.json on Windows.</summary>
    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FileHub",
        "remote-locations.json");

    /// <summary>
    /// Reads every saved location. A missing or unreadable file yields an empty list rather than
    /// an exception: a broken config must not stop the app from starting.
    /// </summary>
    public static IReadOnlyList<RemoteLocation> Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return [];
            var json = File.ReadAllText(ConfigPath);
            if (string.IsNullOrWhiteSpace(json)) return [];

            var stored = JsonSerializer.Deserialize<List<StoredLocation>>(json, JsonOptions);
            if (stored is null) return [];

            var result = new List<RemoteLocation>(stored.Count);
            foreach (var item in stored)
            {
                if (string.IsNullOrWhiteSpace(item.Id)) continue;
                result.Add(new RemoteLocation
                {
                    Id = item.Id,
                    Name = item.Name ?? string.Empty,
                    Protocol = item.Protocol,
                    Host = item.Host ?? string.Empty,
                    Port = item.Port,
                    User = item.User,
                    Password = SecretVault.Unprotect(item.PasswordToken),
                    BasePath = item.BasePath,
                    UseTls = item.UseTls,
                    Region = item.Region,
                    Endpoint = item.Endpoint,
                    SecretKey = SecretVault.Unprotect(item.SecretKeyToken)
                });
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Writes the list back. Passwords are protected by the OS vault first; the file itself only
    /// ever contains tokens. The write is atomic (temp file + replace) so a crash cannot truncate
    /// an existing config.
    /// </summary>
    public static void Save(IEnumerable<RemoteLocation> locations)
    {
        ArgumentNullException.ThrowIfNull(locations);

        var stored = locations.Select(location => new StoredLocation
        {
            Id = location.Id,
            Name = location.Name,
            Protocol = location.Protocol,
            Host = location.Host,
            Port = location.Port,
            User = location.User,
            // The secret is stored under <id>.password so that rotating a password is a single
            // overwrite of the same keychain entry.
            PasswordToken = SecretVault.Protect($"{location.Id}.password", location.Password),
            BasePath = location.BasePath,
            UseTls = location.UseTls,
            Region = location.Region,
            Endpoint = location.Endpoint,
            SecretKeyToken = SecretVault.Protect($"{location.Id}.secret", location.SecretKey)
        }).ToList();

        var directory = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(stored, JsonOptions);
        var temp = ConfigPath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, ConfigPath, overwrite: true);
    }

    /// <summary>JSON shape on disk. Never carries a clear-text password.</summary>
    private sealed class StoredLocation
    {
        public string Id { get; set; } = string.Empty;
        public string? Name { get; set; }
        public RemoteProtocol Protocol { get; set; }
        public string? Host { get; set; }
        public int Port { get; set; }
        public string? User { get; set; }
        public string? PasswordToken { get; set; }
        public string? BasePath { get; set; }
        public bool UseTls { get; set; }
        public string? Region { get; set; }
        public string? Endpoint { get; set; }
        public string? SecretKeyToken { get; set; }
    }
}

/// <summary>
/// Thin wrapper over the platform secret stores. Every method is failure tolerant: a vault that
/// is missing or locked degrades to "missing:" instead of throwing, because a bookmark without a
/// password is still usable (the user gets asked).
/// </summary>
internal static class SecretVault
{
    private const string MissingToken = "missing:";
    private const int TimeoutMs = 8000;

    /// <summary>Stores a secret and returns the token to persist. Null or empty input stays null.</summary>
    public static string? Protect(string account, string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return null;

        if (OperatingSystem.IsWindows())
        {
            var blob = DpapiProtect(secret);
            if (blob is not null) return "dpapi:" + Convert.ToBase64String(blob);
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (TryRun("/usr/bin/security",
                    ["add-generic-password", "-U", "-s", "FileHub", "-a", account, "-w", secret],
                    input: null, out _))
                return "keychain:" + account;
        }
        else if (OperatingSystem.IsLinux())
        {
            if (TryRun("secret-tool",
                    ["store", "--label", "FileHub " + account, "service", "FileHub", "account", account],
                    input: secret, out _))
                return "secret-tool:" + account;
        }

        // Known limitation: without a usable vault we drop the secret. Writing it in clear text
        // (or under a made-up cipher) would be worse than asking the user once per session.
        return MissingToken;
    }

    /// <summary>Resolves a persisted token back to a secret, or null when unavailable.</summary>
    public static string? Unprotect(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        if (token == MissingToken) return null;

        var split = token.IndexOf(':');
        if (split <= 0) return null;
        var scheme = token[..split];
        var payload = token[(split + 1)..];

        switch (scheme)
        {
            case "dpapi":
                if (!OperatingSystem.IsWindows()) return null;
                try
                {
                    return DpapiUnprotect(Convert.FromBase64String(payload));
                }
                catch (FormatException)
                {
                    return null;
                }

            case "keychain":
                if (!OperatingSystem.IsMacOS()) return null;
                return TryRun("/usr/bin/security",
                    ["find-generic-password", "-s", "FileHub", "-a", payload, "-w"],
                    input: null, out var macSecret)
                    ? macSecret.TrimEnd('\r', '\n')
                    : null;

            case "secret-tool":
                if (!OperatingSystem.IsLinux()) return null;
                return TryRun("secret-tool", ["lookup", "service", "FileHub", "account", payload],
                    input: null, out var linuxSecret)
                    ? linuxSecret.TrimEnd('\r', '\n')
                    : null;

            default:
                return null;
        }
    }

    private static bool TryRun(string fileName, string[] arguments, string? input, out string stdout)
    {
        stdout = string.Empty;
        try
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = input is not null,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            if (process is null) return false;

            if (input is not null)
            {
                process.StandardInput.Write(input);
                process.StandardInput.Close();
            }

            stdout = process.StandardOutput.ReadToEnd();
            // Drain stderr so a chatty helper cannot deadlock on a full pipe.
            _ = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(TimeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    private const int CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn, string? description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn, IntPtr description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    /// <summary>Encrypts with DPAPI under the current user profile; null when the call fails.</summary>
    private static byte[]? DpapiProtect(string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        var input = new DataBlob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            if (!CryptProtectData(ref input, "FileHub", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out var output))
                return null;
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
        }
    }

    /// <summary>Decrypts a DPAPI blob; null when it was produced by another user or machine.</summary>
    private static string? DpapiUnprotect(byte[] blob)
    {
        var input = new DataBlob { Size = blob.Length, Data = Marshal.AllocHGlobal(blob.Length) };
        try
        {
            Marshal.Copy(blob, 0, input.Data, blob.Length);
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out var output))
                return null;
            try
            {
                var bytes = new byte[output.Size];
                Marshal.Copy(output.Data, bytes, 0, output.Size);
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
        }
    }
}

/// <summary>
/// Write stream that stages bytes in a temp file and hands the file to a protocol-specific
/// uploader on dispose. SMB, WebDAV and S3 all need a known content length and a hash before the
/// request starts, and none of them can be trusted with a half-written file, so staging is the
/// simplest correct shape.
/// </summary>
internal sealed class RemoteStagedWriteStream : Stream
{
    private readonly string _tempPath;
    private readonly FileStream _file;
    private readonly Func<string, CancellationToken, Task> _commit;
    private readonly CancellationToken _cancellationToken;
    private bool _finished;

    public RemoteStagedWriteStream(Func<string, CancellationToken, Task> commit, CancellationToken cancellationToken)
    {
        _commit = commit;
        _cancellationToken = cancellationToken;
        _tempPath = Path.Combine(Path.GetTempPath(), "filehub-upload-" + Guid.NewGuid().ToString("n") + ".tmp");
        _file = new FileStream(_tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            64 * 1024, FileOptions.Asynchronous);
    }

    public override bool CanRead => false;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => _file.Length;

    public override long Position
    {
        get => _file.Position;
        set => _file.Position = value;
    }

    public override void Flush() => _file.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _file.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count)
        => throw new NotSupportedException("The upload stream is write-only");

    public override long Seek(long offset, SeekOrigin origin) => _file.Seek(offset, origin);

    public override void SetLength(long value) => _file.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => _file.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => _file.Write(buffer);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _file.WriteAsync(buffer, offset, count, cancellationToken);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => _file.WriteAsync(buffer, cancellationToken);

    public override async ValueTask DisposeAsync()
    {
        if (_finished) return;
        _finished = true;
        await _file.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        await _file.DisposeAsync().ConfigureAwait(false);
        try
        {
            await _commit(_tempPath, _cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteTemp();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing || _finished) return;
        _finished = true;
        _file.Dispose();
        try
        {
            // Callers that use the synchronous "using" form still have to complete the upload;
            // blocking here is the only way to surface the failure to them.
            _commit(_tempPath, _cancellationToken).GetAwaiter().GetResult();
        }
        finally
        {
            TryDeleteTemp();
        }
    }

    private void TryDeleteTemp()
    {
        try
        {
            if (File.Exists(_tempPath)) File.Delete(_tempPath);
        }
        catch (IOException)
        {
            // A leftover temp file is harmless; the OS cleans it up eventually.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Read-only stream over an HTTP response body that disposes the owning response when the caller
/// is done, so the connection is returned to the pool at the right time.
/// </summary>
internal sealed class RemoteHttpStream : Stream
{
    private readonly Stream _inner;
    private readonly HttpResponseMessage _response;
    private readonly bool _ownsResponse;

    public RemoteHttpStream(Stream inner, HttpResponseMessage? response)
    {
        _inner = inner;
        _response = response ?? new HttpResponseMessage();
        _ownsResponse = response is not null;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => throw new NotSupportedException();
    }

    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => _inner.Read(buffer);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
            if (_ownsResponse) _response.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        if (_ownsResponse) _response.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
