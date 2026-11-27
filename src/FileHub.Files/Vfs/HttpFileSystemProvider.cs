// Copyright (c) 2026 Hub-code-develop. Licensed under the MIT License.
//
// Browsing a plain http(s) URL as a location.
//
// A web server has no file-system API, but it usually exposes one of two things that can be
// turned into a listing:
//
//   * WebDAV (PROPFIND returns 207 Multi-Status) - many NAS boxes, Nextcloud, Apache with mod_dav.
//   * an HTML directory index - nginx and Apache autoindex, `python -m http.server`, most mirrors.
//
// ListAsync tries PROPFIND first and falls back to reading the anchor tags of the index page.
// When neither works the URL is a web page, not a folder, and the caller is told so in plain
// language instead of being shown an empty list.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FileHub.Files.Models;

namespace FileHub.Files.Vfs;

/// <summary>What a URL actually points at, as far as one HEAD (plus one probe) can tell.</summary>
public sealed record HttpResource(
    bool Exists,
    bool IsDirectory,
    long Size,
    DateTimeOffset Modified,
    string ContentType,
    string FinalUrl,
    string? SuggestedName = null);

/// <summary>
/// Read/write access to an http(s) location. Reading is always available; writing uses PUT,
/// MKCOL, DELETE and MOVE, so it works against WebDAV endpoints and rejects cleanly (405) on
/// plain static servers.
/// </summary>
public sealed class HttpFileSystemProvider : IFileSystemProvider
{
    private const long MaxStagedBytes = 8L * 1024 * 1024 * 1024;

    private static readonly HttpClient Client = CreateClient();
    private static readonly ConcurrentDictionary<string, (string User, string Password)> Credentials =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, (bool Result, DateTimeOffset At)> ListingCache = [];
    private static readonly Regex AnchorRegex = new(
        "<a\\b[^>]*?href\\s*=\\s*(?:\"(?<u>[^\"]*)\"|'(?<u>[^']*)'|(?<u>[^\\s>]+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseCookies = true
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FileHub/1.0 (+https://github.com/Hub-code-develop/FileHub)");
        return client;
    }

    /// <summary>Creates a provider for one of the two URL schemes.</summary>
    public HttpFileSystemProvider(VfsScheme scheme)
    {
        if (scheme is not (VfsScheme.Http or VfsScheme.Https))
            throw new ArgumentException("HTTP provider only serves http and https paths", nameof(scheme));
        Scheme = scheme;
    }

    public VfsScheme Scheme { get; }

    public string DisplayName => "Web";

    /// <summary>
    /// Registers Basic credentials for a host. The address bar calls this when a URL carries
    /// `user:pass@`, so the password never has to be stored in the breadcrumb path.
    /// </summary>
    public static void SetCredentials(string authority, string user, string password)
        => Credentials[authority] = (user, password);

    /// <summary>Describes the resource behind a canonical URL without listing it.</summary>
    public async Task<HttpResource> DescribeAsync(string url, CancellationToken ct = default)
        => await DescribeAsync(VfsPath.Parse(url), ct).ConfigureAwait(false);

    public async Task<VfsStat> StatAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var resource = await DescribeAsync(path, cancellationToken).ConfigureAwait(false);
        return resource.Exists
            ? new VfsStat(true, resource.IsDirectory, resource.Size, resource.Modified)
            : VfsStat.Missing;
    }

    public async Task<IReadOnlyList<FileEntry>> ListAsync(
        VfsPath path, bool includeHidden, CancellationToken cancellationToken)
    {
        var uri = BuildUri(path);
        var baseUri = EnsureDirectoryUri(uri);

        List<FileEntry>? entries = null;

        var dav = await PropfindAsync(baseUri, depthOne: true, cancellationToken).ConfigureAwait(false);
        if (dav is not null) entries = dav.Entries;

        if (entries is null)
        {
            var links = await ReadHtmlLinksAsync(baseUri, cancellationToken).ConfigureAwait(false);
            if (links is null)
                throw new InvalidDataException(
                    $"'{baseUri}' does not expose a WebDAV collection or an HTML index, so it has " +
                    "nothing to browse. Open it in a browser instead.");

            entries = [];
            foreach (var link in links)
                entries.Add(BuildEntry(path, link.Uri, link.IsDirectory, 0, default));
        }

        var filtered = includeHidden
            ? entries
            : entries.Where(x => !x.Name.StartsWith('.')).ToList();

        return filtered
            .OrderByDescending(x => x.IsDirectory)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Downloads the resource into a seekable temporary file. Office, Monaco and the preview
    /// parsers all expect to seek, and a raw HTTP stream cannot.
    /// </summary>
    public async Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var uri = BuildUri(path);
        var folder = Path.Combine(Path.GetTempPath(), "FileHub", "web");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"fh-{Guid.NewGuid():n}{path.Extension}");

        using var response = await SendAsync(HttpMethod.Get, uri, cancellationToken,
            HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        EnsureSuccess(response, uri);

        var stream = new FileStream(file, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
            128 * 1024, FileOptions.DeleteOnClose | FileOptions.SequentialScan);
        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await CopyAsync(source, stream, cancellationToken).ConfigureAwait(false);
            stream.Position = 0;
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Streams a URL straight to a local file without staging it twice. Used by the URL cache,
    /// which only needs the bytes on disk.
    /// </summary>
    public static async Task DownloadToAsync(
        string url, string targetFile, IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var uri = new Uri(url);
        using var response = await SendAsync(HttpMethod.Get, uri, cancellationToken,
            HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        EnsureSuccess(response, uri);

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var destination = new FileStream(targetFile, FileMode.Create, FileAccess.Write,
            FileShare.None, 128 * 1024, FileOptions.Asynchronous);

        var buffer = new byte[128 * 1024];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxStagedBytes)
                throw new IOException("The download is larger than 8 GB and was stopped.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            progress?.Report(total);
        }
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<Stream> CreateFileAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var uri = BuildUri(path);
        return Task.FromResult<Stream>(new RemoteStagedWriteStream(
            (staged, ct) => PutFileAsync(uri, staged, ct), cancellationToken));
    }

    public async Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var uri = EnsureDirectoryUri(BuildUri(path));
        using var request = new HttpRequestMessage(new HttpMethod("MKCOL"), uri);
        using var response = await SendAsync(request, uri, cancellationToken).ConfigureAwait(false);
        // 405 means the collection already exists; that is the result the caller wanted.
        if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.Conflict) return;
        EnsureSuccess(response, uri);
    }

    public async Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken)
    {
        var uri = BuildUri(path);
        using var response = await SendAsync(HttpMethod.Delete, uri, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        EnsureSuccess(response, uri);
    }

    public async Task RenameAsync(VfsPath path, string newName, CancellationToken cancellationToken)
    {
        var parent = path.Parent
            ?? throw new NotSupportedException("The site root cannot be renamed.");
        await MoveAsync(path, parent.Combine(newName), cancellationToken).ConfigureAwait(false);
    }

    public async Task MoveAsync(VfsPath source, VfsPath target, CancellationToken cancellationToken)
    {
        // Name collisions are resolved by VfsFileSystem before it calls the provider, so the
        // destination here is exact and must not be overwritten implicitly.
        var from = BuildUri(source);
        var to = BuildUri(target);

        using var request = new HttpRequestMessage(new HttpMethod("MOVE"), from);
        request.Headers.TryAddWithoutValidation("Destination", to.AbsoluteUri);
        request.Headers.TryAddWithoutValidation("Overwrite", "F");
        using var response = await SendAsync(request, from, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, from);
    }

    private async Task<HttpResource> DescribeAsync(VfsPath path, CancellationToken ct)
    {
        var uri = BuildUri(path);
        long length = -1;
        DateTimeOffset modified = default;
        var contentType = string.Empty;
        var status = HttpStatusCode.OK;
        var finalUrl = uri.AbsoluteUri;
        string? suggestedName = null;

        using (var head = await SendAsync(HttpMethod.Head, uri, ct).ConfigureAwait(false))
        {
            status = head.StatusCode;
            length = head.Content.Headers.ContentLength ?? -1;
            modified = head.Content.Headers.LastModified ?? default;
            contentType = head.Content.Headers.ContentType?.MediaType ?? string.Empty;
            finalUrl = head.RequestMessage?.RequestUri?.AbsoluteUri ?? uri.AbsoluteUri;
            suggestedName = NameFromDisposition(head.Content.Headers.ContentDisposition);
        }

        // Plenty of CDNs and object stores refuse HEAD but serve GET perfectly well.
        if (status is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented
            or HttpStatusCode.Forbidden)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = new RangeHeaderValue(0, 0);
            using var probe = await SendCoreAsync(request, uri, ct, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);
            status = probe.StatusCode;
            length = probe.Content.Headers.ContentRange?.Length ?? probe.Content.Headers.ContentLength ?? -1;
            modified = probe.Content.Headers.LastModified ?? default;
            contentType = probe.Content.Headers.ContentType?.MediaType ?? string.Empty;
            finalUrl = probe.RequestMessage?.RequestUri?.AbsoluteUri ?? uri.AbsoluteUri;
            suggestedName = NameFromDisposition(probe.Content.Headers.ContentDisposition) ?? suggestedName;
        }

        if (status is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            return new HttpResource(false, false, 0, default, string.Empty, uri.AbsoluteUri);

        if ((int)status >= 400) throw CreateException(status, uri);

        var isDirectory = uri.AbsolutePath.EndsWith('/') || finalUrl.EndsWith('/');
        if (!isDirectory && IsHtml(contentType))
            isDirectory = await LooksLikeListingAsync(uri, ct).ConfigureAwait(false);

        return new HttpResource(
            true,
            isDirectory,
            isDirectory || length < 0 ? 0 : length,
            modified,
            contentType,
            finalUrl,
            suggestedName);
    }

    private async Task<bool> LooksLikeListingAsync(Uri uri, CancellationToken ct)
    {
        var key = uri.AbsoluteUri;
        if (ListingCache.TryGetValue(key, out var cached) &&
            DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(2))
            return cached.Result;

        bool result;
        var dav = await PropfindAsync(EnsureDirectoryUri(uri), depthOne: false, ct).ConfigureAwait(false);
        if (dav is not null) result = dav.IsCollection || dav.Entries.Count > 0;
        else
        {
            var links = await ReadHtmlLinksAsync(EnsureDirectoryUri(uri), ct).ConfigureAwait(false);
            result = links is { Count: > 0 };
        }

        ListingCache[key] = (result, DateTimeOffset.UtcNow);
        return result;
    }

    /// <summary>
    /// Reads a WebDAV multi-status response. Returns null when the server does not speak DAV,
    /// so the caller can fall back to HTML rather than showing an error.
    /// </summary>
    private async Task<PropfindResult?> PropfindAsync(Uri dirUri, bool depthOne, CancellationToken ct)
    {
        const string body =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<d:propfind xmlns:d=\"DAV:\"><d:prop>" +
            "<d:resourcetype/><d:getcontentlength/><d:getlastmodified/>" +
            "</d:prop></d:propfind>";

        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), dirUri)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/xml")
        };
        request.Headers.TryAddWithoutValidation("Depth", depthOne ? "1" : "0");

        using var response = await SendAsync(request, dirUri, ct).ConfigureAwait(false);
        if (response.StatusCode != (HttpStatusCode)207) return null;

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var document = await XDocument.LoadAsync(stream, LoadOptions.None, ct).ConfigureAwait(false);

            var entries = new List<FileEntry>();
            var isCollection = false;
            var basePath = dirUri.AbsolutePath;
            var dirPath = VfsPath.Parse(dirUri.AbsoluteUri);

            foreach (var node in document.Descendants())
            {
                if (!string.Equals(node.Name.LocalName, "response", StringComparison.Ordinal)) continue;

                var href = node.Elements()
                    .FirstOrDefault(x => string.Equals(x.Name.LocalName, "href", StringComparison.Ordinal))
                    ?.Value;
                if (string.IsNullOrWhiteSpace(href)) continue;
                if (!Uri.TryCreate(dirUri, href.Trim(), out var child)) continue;
                if (!string.Equals(child.Host, dirUri.Host, StringComparison.OrdinalIgnoreCase)) continue;

                var collection = node.Descendants().Any(x =>
                    string.Equals(x.Name.LocalName, "collection", StringComparison.Ordinal));
                if (collection && string.Equals(child.AbsolutePath.TrimEnd('/'),
                        basePath.TrimEnd('/'), StringComparison.Ordinal))
                {
                    isCollection = true;
                    continue;
                }

                if (!child.AbsolutePath.StartsWith(basePath, StringComparison.Ordinal)) continue;
                if (string.Equals(child.AbsolutePath.TrimEnd('/'), basePath.TrimEnd('/'),
                        StringComparison.Ordinal)) continue;

                var size = ParseLong(node.Descendants()
                    .FirstOrDefault(x => x.Name.LocalName == "getcontentlength")?.Value);
                var modified = ParseDate(node.Descendants()
                    .FirstOrDefault(x => x.Name.LocalName == "getlastmodified")?.Value);

                entries.Add(BuildEntry(dirPath, child, collection, size, modified));
            }

            return new PropfindResult(isCollection, entries);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException)
        {
            // A 207 with an unparsable body is not a WebDAV server either.
            return null;
        }
    }

    private async Task<List<(Uri Uri, bool IsDirectory)>?> ReadHtmlLinksAsync(Uri dirUri, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, dirUri, ct,
            HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (!IsHtml(mediaType) && mediaType.Length > 0) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 16 * 1024, leaveOpen: true);
        var buffer = new char[256 * 1024];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
        if (read <= 0) return null;

        var basePath = EnsureSlash(dirUri.AbsolutePath);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var links = new List<(Uri, bool)>();

        foreach (Match match in AnchorRegex.Matches(new string(buffer, 0, read)))
        {
            var href = WebUtility.HtmlDecode(match.Groups["u"].Value).Trim();
            if (href.Length == 0 || href[0] is '?' or '#') continue;
            if (href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                href.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!Uri.TryCreate(dirUri, href, out var child)) continue;
            if (child.Scheme is not ("http" or "https")) continue;
            if (!string.Equals(child.Host, dirUri.Host, StringComparison.OrdinalIgnoreCase)) continue;
            if (child.Port != dirUri.Port) continue;

            var path = child.AbsolutePath;
            if (!path.StartsWith(basePath, StringComparison.Ordinal)) continue;
            if (string.Equals(path, basePath, StringComparison.Ordinal)) continue;
            if (!seen.Add(path)) continue;

            links.Add((child, path.EndsWith('/')));
        }

        return links;
    }

    private static FileEntry BuildEntry(
        VfsPath dir, Uri child, bool isDirectory, long size, DateTimeOffset modified)
    {
        var path = isDirectory ? EnsureSlash(child.AbsolutePath) : child.AbsolutePath;
        var trimmed = path.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        var name = Uri.UnescapeDataString(slash < 0 ? trimmed : trimmed[(slash + 1)..]);
        if (name.Length == 0) name = child.Host;

        return new FileEntry
        {
            Name = name,
            FullPath = new VfsPath(dir.Scheme, dir.Authority, path).Canonical,
            IsDirectory = isDirectory,
            IsHidden = name.StartsWith('.'),
            Size = isDirectory ? 0 : size,
            LastWriteTime = modified
        };
    }

    private async Task PutFileAsync(Uri uri, string stagedFile, CancellationToken ct)
    {
        await using var file = new FileStream(stagedFile, FileMode.Open, FileAccess.Read,
            FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var content = new StreamContent(file);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(GuessContentType(uri.AbsolutePath));
        using var request = new HttpRequestMessage(HttpMethod.Put, uri) { Content = content };
        using var response = await SendAsync(request, uri, ct).ConfigureAwait(false);
        EnsureSuccess(response, uri);
    }

    internal static async Task CopyAsync(Stream source, Stream destination, CancellationToken ct)
    {
        var buffer = new byte[128 * 1024];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxStagedBytes)
                throw new IOException("The file is larger than 8 GB and cannot be staged locally.");
            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        await destination.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, Uri uri, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        using var request = new HttpRequestMessage(method, uri);
        return await SendCoreAsync(request, uri, ct, completion).ConfigureAwait(false);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, Uri uri, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
        => await SendCoreAsync(request, uri, ct, completion).ConfigureAwait(false);

    private static async Task<HttpResponseMessage> SendCoreAsync(
        HttpRequestMessage request, Uri uri, CancellationToken ct, HttpCompletionOption completion)
    {
        if (Credentials.TryGetValue(uri.Authority, out var credential))
        {
            var raw = Encoding.UTF8.GetBytes($"{credential.User}:{credential.Password}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
        }

        try
        {
            return await Client.SendAsync(request, completion, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"The server at {uri.Host} did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            throw new IOException($"Cannot reach {uri.Host}: {ex.Message}", ex);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response, Uri uri)
    {
        if (response.IsSuccessStatusCode) return;
        throw CreateException(response.StatusCode, uri);
    }

    private static Exception CreateException(HttpStatusCode status, Uri uri) => status switch
    {
        HttpStatusCode.Unauthorized => new UnauthorizedAccessException(
            $"HTTP 401: credentials rejected for {uri.Host}"),
        HttpStatusCode.Forbidden => new UnauthorizedAccessException(
            $"HTTP 403: access denied for {uri.AbsolutePath}"),
        HttpStatusCode.NotFound or HttpStatusCode.Gone => new FileNotFoundException(
            $"HTTP 404: {uri} was not found"),
        HttpStatusCode.MethodNotAllowed => new NotSupportedException(
            $"The server at {uri.Host} does not allow this operation on {uri.AbsolutePath}"),
        _ => new HttpRequestException(
            $"HTTP {(int)status} {status} for {uri}")
    };

    private static Uri BuildUri(VfsPath path)
    {
        if (path.Scheme is not (VfsScheme.Http or VfsScheme.Https))
            throw new ArgumentException($"{path.Scheme} is not an HTTP location", nameof(path));
        if (string.IsNullOrWhiteSpace(path.Authority))
            throw new ArgumentException("A web location needs a host", nameof(path));

        var inner = path.Inner.Length == 0 ? "/" : path.Inner;
        if (!Uri.TryCreate($"{VfsPath.Token(path.Scheme)}://{path.Authority}{inner}", UriKind.Absolute, out var uri))
            throw new ArgumentException($"'{path.Canonical}' is not a valid URL", nameof(path));
        return uri;
    }

    private static Uri EnsureDirectoryUri(Uri uri)
        => uri.AbsolutePath.EndsWith('/') ? uri : new Uri(uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath + "/");

    private static string EnsureSlash(string path) => path.EndsWith('/') ? path : path + "/";

    private static bool IsHtml(string mediaType)
        => mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
           || mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);

    private static string? NameFromDisposition(ContentDispositionHeaderValue? disposition)
    {
        var name = disposition?.FileNameStar ?? disposition?.FileName;
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = name.Trim().Trim('"');
        return name.Length == 0 ? null : name;
    }

    private static long ParseLong(string? value)
        => long.TryParse(value, out var parsed) ? parsed : 0;

    private static DateTimeOffset ParseDate(string? value)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed : default;

    private static string GuessContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".txt" or ".log" or ".md" => "text/plain; charset=utf-8",
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".json" => "application/json",
        ".xml" => "application/xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".pdf" => "application/pdf",
        ".zip" => "application/zip",
        _ => "application/octet-stream"
    };

    private sealed record PropfindResult(bool IsCollection, List<FileEntry> Entries);
}
