// Copyright (c) 2026 Hub-code-develop. Licensed under the MIT License.
//
// Local cache for files pulled off the web. Pasting a URL into the address bar downloads the
// resource here, so the Office page, Monaco and the previewers all get a real file handle.
// Keeping it on disk (rather than in the temp folder) means a second visit is instant and the
// user can find what they downloaded.

using FileHub.Files.Vfs;

namespace FileHub.Files.Services;

public static class WebCacheService
{
    private static readonly char[] Invalid = Path.GetInvalidFileNameChars();

    /// <summary>Per-user cache root, e.g. ~/Library/Caches/FileHub/web on macOS.</summary>
    public static string Root { get; } = CreateRoot();

    private static string CreateRoot()
    {
        string baseDirectory;
        if (OperatingSystem.IsMacOS())
            baseDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches");
        else if (OperatingSystem.IsWindows())
            baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        else
            baseDirectory = Environment.GetEnvironmentVariable("XDG_CACHE_HOME")
                            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");

        return Path.Combine(baseDirectory, "FileHub", "web");
    }

    /// <summary>Where a URL is stored on disk. Stable across sessions so the cache can be reused.</summary>
    public static string PathFor(string url, string? suggestedName = null)
    {
        var path = VfsPath.Parse(url);
        var host = Sanitize(path.Authority, "host");
        var folder = Path.Combine(Root, host);

        var segments = path.Inner.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
            folder = Path.Combine(folder, Sanitize(Uri.UnescapeDataString(segments[i]), "folder"));

        var name = suggestedName;
        if (string.IsNullOrWhiteSpace(name) && segments.Length > 0)
            name = Uri.UnescapeDataString(segments[^1]);
        if (string.IsNullOrWhiteSpace(name)) name = "index.html";

        return Path.Combine(folder, Sanitize(name, "download"));
    }

    /// <summary>
    /// Downloads a URL into the cache and returns the local path. An existing copy is reused
    /// unless <paramref name="force"/> is set; progress is reported as 0..1 when the server
    /// sends a content length.
    /// </summary>
    public static async Task<string> DownloadAsync(
        string url, IProgress<double>? progress = null, bool force = false,
        CancellationToken cancellationToken = default)
    {
        var target = PathFor(url);
        if (!force && File.Exists(target) && new FileInfo(target).Length > 0)
            return target;

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".part";

        var resource = await new HttpFileSystemProvider(
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? VfsScheme.Https : VfsScheme.Http)
            .DescribeAsync(url, cancellationToken).ConfigureAwait(false);
        var total = resource.Size;

        await HttpFileSystemProvider.DownloadToAsync(url, temp, new Progress<long>(bytes =>
        {
            if (total > 0) progress?.Report(Math.Clamp((double)bytes / total, 0, 1));
        }), cancellationToken).ConfigureAwait(false);

        File.Move(temp, target, overwrite: true);
        progress?.Report(1);
        return target;
    }

    /// <summary>Drops everything the address bar has downloaded so far.</summary>
    public static void Clear()
    {
        if (!Directory.Exists(Root)) return;
        if (!string.Equals(Path.GetFileName(Root), "web", StringComparison.Ordinal)) return;
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string Sanitize(string name, string fallback)
    {
        var cleaned = new string(name.Select(c => Invalid.Contains(c) || c is '<' or '>' or ':' or '"' or '|' or '?' or '*'
            ? '_'
            : c).ToArray()).Trim().TrimEnd('.');
        if (cleaned.Length == 0 || cleaned == "." || cleaned == "..") cleaned = fallback;
        return cleaned.Length > 120 ? cleaned[..120] : cleaned;
    }
}
