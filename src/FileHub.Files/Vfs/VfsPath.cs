// Copyright (c) 2026 Hub-code-develop. Licensed under the MIT License.
//
// Virtual path model. FileHub addresses more than the local disk: an entry inside a zip, a
// share on a NAS, a bucket key. All of them must flow through the same navigation stack,
// breadcrumb and file operations, so every location has one canonical string form.
//
//   local     /Users/me/notes
//   archive   archive:///Users/me/pack.zip!/docs/readme.md
//   smb       smb://nas.local/share/folder
//   sftp      sftp://user@host/var/www
//   webdav    webdav://dav.example.com/remote.php/dav/files/me
//   s3        s3://bucket/prefix
//
// The canonical form is deliberately path-shaped so existing helpers (Path.GetFileName) keep
// working; anything that needs the real location uses Parse.

namespace FileHub.Files.Vfs;

public enum VfsScheme
{
    Local,
    Archive,
    Smb,
    Sftp,
    WebDav,
    S3,
    Plugin
}

public readonly record struct VfsPath(VfsScheme Scheme, string Authority, string Inner)
{
    public bool IsLocal => Scheme == VfsScheme.Local;
    public bool IsVirtual => Scheme != VfsScheme.Local;

    public static VfsPath Local(string path) => new(VfsScheme.Local, string.Empty, path);

    public static string Token(VfsScheme scheme) => scheme switch
    {
        VfsScheme.Local => "file",
        VfsScheme.Archive => "archive",
        VfsScheme.Smb => "smb",
        VfsScheme.Sftp => "sftp",
        VfsScheme.WebDav => "webdav",
        VfsScheme.S3 => "s3",
        _ => "plugin"
    };

    /// <summary>Maps a URI scheme token to its enum value. Unknown tokens are plugin schemes.</summary>
    public static bool TryParseScheme(string token, out VfsScheme scheme)
    {
        switch (token.ToLowerInvariant())
        {
            case "file": scheme = VfsScheme.Local; return true;
            case "archive": scheme = VfsScheme.Archive; return true;
            case "smb": scheme = VfsScheme.Smb; return true;
            case "sftp": scheme = VfsScheme.Sftp; return true;
            case "webdav": scheme = VfsScheme.WebDav; return true;
            case "s3": scheme = VfsScheme.S3; return true;
            case "plugin": scheme = VfsScheme.Plugin; return true;
            default: scheme = VfsScheme.Plugin; return false;
        }
    }

    public string Canonical
    {
        get
        {
            if (IsLocal) return Inner;
            var inner = Inner.Length == 0 ? "/" : Inner.StartsWith('/') ? Inner : "/" + Inner;
            return Scheme == VfsScheme.Archive
                ? $"{Token(Scheme)}://{Authority}!/{inner.TrimStart('/')}"
                : $"{Token(Scheme)}://{Authority}{inner}";
        }
    }

    public override string ToString() => Canonical;

    /// <summary>
    /// True when the string carries a virtual scheme. A Windows drive letter ("C:\") and a
    /// POSIX path both contain ':' but never "://", so the marker is unambiguous.
    /// </summary>
    public static bool IsVirtualPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var idx = path.IndexOf("://", StringComparison.Ordinal);
        if (idx <= 0) return false;
        // A URI scheme is alpha + alnum/+-. ; anything else is a plain path that happens to
        // contain the sequence.
        for (var i = 0; i < idx; i++)
        {
            var c = path[i];
            var ok = char.IsAsciiLetter(c) || (i > 0 && (char.IsAsciiDigit(c) || c is '+' or '-' or '.'));
            if (!ok) return false;
        }
        return true;
    }

    public static VfsPath Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) throw new ArgumentException("Path cannot be empty", nameof(raw));
        if (!IsVirtualPath(raw)) return Local(raw);

        var idx = raw.IndexOf("://", StringComparison.Ordinal);
        var token = raw[..idx];
        TryParseScheme(token, out var scheme);
        var rest = raw[(idx + 3)..];

        if (scheme == VfsScheme.Archive)
        {
            // The container path may itself contain '!' but not "!/", which is the separator.
            var bang = rest.IndexOf("!/", StringComparison.Ordinal);
            if (bang < 0) return new VfsPath(scheme, rest, "/");
            var inner = rest[(bang + 1)..];
            return new VfsPath(scheme, rest[..bang], inner.Length == 0 ? "/" : inner);
        }

        var slash = rest.IndexOf('/');
        if (slash < 0) return new VfsPath(scheme, rest, "/");
        var innerPath = rest[slash..];
        return new VfsPath(scheme, rest[..slash], innerPath.Length == 0 ? "/" : innerPath);
    }

    /// <summary>Last path segment, or the authority when the inner path is the root.</summary>
    public string FileName
    {
        get
        {
            if (IsLocal)
            {
                var trimmed = Inner.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return Path.GetFileName(trimmed);
            }
            var p = Inner.Trim('/');
            if (p.Length == 0) return Authority;
            var last = p.LastIndexOf('/');
            return last < 0 ? p : p[(last + 1)..];
        }
    }

    public string Extension
    {
        get
        {
            var name = FileName;
            var dot = name.LastIndexOf('.');
            return dot <= 0 ? string.Empty : name[dot..];
        }
    }

    /// <summary>Parent location, or null at a root (a volume root, a share root, an archive root).</summary>
    public VfsPath? Parent
    {
        get
        {
            if (IsLocal)
            {
                var parent = Path.GetDirectoryName(Inner.TrimEnd(Path.DirectorySeparatorChar));
                return parent is null ? null : Local(parent);
            }

            var p = Inner.Trim('/');
            if (p.Length == 0) return null;
            var slash = p.LastIndexOf('/');
            if (slash < 0) return new VfsPath(Scheme, Authority, "/");
            return new VfsPath(Scheme, Authority, "/" + p[..slash]);
        }
    }

    /// <summary>Absolute path of the backing container for archive locations, else null.</summary>
    public string? ContainerPath => Scheme == VfsScheme.Archive ? Authority : null;

    /// <summary>Joins a single child name onto this location.</summary>
    public VfsPath Combine(string child)
    {
        if (IsLocal) return Local(Path.Combine(Inner, child));
        return new VfsPath(Scheme, Authority, Inner.TrimEnd('/') + "/" + child.TrimStart('/'));
    }

    /// <summary>
    /// Breadcrumb segments from the outermost anchor to this location. An archive shows the
    /// container file name first, then its inner folders.
    /// </summary>
    public IReadOnlyList<(string Name, string Path)> Segments()
    {
        var result = new List<(string Name, string Path)>();
        if (IsLocal)
        {
            var full = Inner;
            var root = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(root)) result.Add((root, root));
            var rest = full[(root?.Length ?? 0)..];
            foreach (var part in rest.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                var soFar = result.Count == 0 ? part : Path.Combine(result[^1].Path, part);
                result.Add((part, soFar));
            }
            return result;
        }

        // Anchor: the container file (archive) or authority (remote).
        var anchorPath = new VfsPath(Scheme, Authority, "/").Canonical;
        var anchorName = Scheme == VfsScheme.Archive
            ? Path.GetFileName(Authority)
            : $"{Token(Scheme)}://{Authority}";
        result.Add((string.IsNullOrEmpty(anchorName) ? Authority : anchorName, anchorPath));

        var inner = Inner.Trim('/');
        if (inner.Length == 0) return result;
        var parts = inner.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Build forward so each segment's canonical path is complete.
        for (var i = 0; i < parts.Length; i++)
        {
            var path = new VfsPath(Scheme, Authority, "/" + string.Join('/', parts[..(i + 1)])).Canonical;
            result.Add((parts[i], path));
        }
        return result;
    }
}
