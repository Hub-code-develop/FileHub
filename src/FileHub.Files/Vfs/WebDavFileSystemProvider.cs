// Copyright (c) 2026 Hub-code-develop. Licensed under the MIT License.
//
// WebDAV location. The protocol is small enough to speak directly over HttpClient: PROPFIND for
// listing and stat, GET/PUT for content, MKCOL/DELETE/MOVE for structure. The only real trap is
// the 207 XML, whose namespace prefix is chosen by the server (D:, d:, lp1:, none at all), so
// every element is matched by LocalName instead of a hard-coded namespace.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using FileHub.Files.Models;

namespace FileHub.Files.Vfs;

public sealed class WebDavFileSystemProvider : IFileSystemProvider, IDisposable
{
    private const string PropFindBody = """
        <?xml version="1.0" encoding="utf-8"?>
        <d:propfind xmlns:d="DAV:">
          <d:prop>
            <d:displayname/>
            <d:getcontentlength/>
            <d:getlastmodified/>
            <d:resourcetype/>
          </d:prop>
        </d:propfind>
        """;

    private static readonly HttpMethod PropFindMethod = new("PROPFIND");

    private readonly RemoteLocation _location;
    private readonly HttpClient _http;
    private readonly string _origin;
    private readonly string _originPath;
    private readonly string? _authorization;
    private bool _disposed;

    /// <summary>Creates a provider for one saved WebDAV bookmark.</summary>
    /// <exception cref="InvalidOperationException">The location has no host or no usable password.</exception>
    public WebDavFileSystemProvider(RemoteLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (string.IsNullOrWhiteSpace(location.Host))
            throw new InvalidOperationException("A WebDAV location needs a host name");
        if (string.IsNullOrEmpty(location.Password) && !string.IsNullOrEmpty(location.User))
            throw new InvalidOperationException(
                $"No password is available for '{location.Name}'. Re-enter the credentials for this WebDAV location.");

        _location = location;

        var scheme = location.UseTls ? "https" : "http";
        var port = location.ResolvePort();
        var defaultPort = location.UseTls ? 443 : 80;
        var host = port == defaultPort ? location.Host : $"{location.Host}:{port}";
        _origin = $"{scheme}://{host}/";
        _originPath = new Uri(_origin).AbsolutePath;

        if (!string.IsNullOrEmpty(location.User))
        {
            var raw = $"{location.User}:{location.Password}";
            _authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
        }

        var handler = new SocketsHttpHandler
        {
            PreAuthenticate = true,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        if (!string.IsNullOrEmpty(location.User))
            handler.Credentials = new NetworkCredential(location.User, location.Password);

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("FileHub/1.0");
        if (_authorization is not null)
            _http.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse(_authorization);
    }

    /// <summary>The bookmark this provider serves.</summary>
    public RemoteLocation Location => _location;

    /// <inheritdoc/>
    public VfsScheme Scheme => VfsScheme.WebDav;

    /// <inheritdoc/>
    public string DisplayName => "WebDAV";

    public async Task<IReadOnlyList<FileEntry>> ListAsync(VfsPath path, bool includeHidden, CancellationToken cancellationToken)
    {
        var requestPath = BuildUrl(path, asCollection: true);
        using var request = new HttpRequestMessage(PropFindMethod, requestPath);
        request.Headers.TryAddWithoutValidation("Depth", "1");
        request.Content = new StringContent(PropFindBody, Encoding.UTF8, "application/xml");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, "PROPFIND", requestPath, cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var entries = ParseMultiStatus(body);
        var self = NormalizePath(new Uri(requestPath).AbsolutePath);

        var result = new List<FileEntry>(entries.Count);
        foreach (var entry in entries)
        {
            var relative = ToRelativePath(entry.Href);
            if (relative is null) continue;
            if (NormalizePath(new Uri(new Uri(_origin), entry.Href).AbsolutePath) == self) continue;
            if (relative.Length == 0) continue;

            var name = string.IsNullOrWhiteSpace(entry.DisplayName)
                ? relative[(relative.LastIndexOf('/') + 1)..]
                : entry.DisplayName!;
            if (name.Length == 0) continue;

            var isHidden = name.StartsWith('.');
            if (isHidden && !includeHidden) continue;

            var virtualPath = new VfsPath(VfsScheme.WebDav, _location.Authority, "/" + relative);
            result.Add(new FileEntry
            {
                Name = name,
                FullPath = virtualPath.Canonical,
                IsDirectory = entry.IsDirectory,
                IsHidden = isHidden,
                Size = entry.IsDirectory ? 0 : entry.Size,
                LastWriteTime = entry.Modified
            });
        }

        return result;
    }

    public async Task<VfsStat> StatAsync(VfsPath path, CancellationToken cancellationToken)
    {
        // Collections and plain resources are addressed differently by most servers: a trailing
        // slash on a file is a 404, and a missing slash on a collection can be a 301. Try the
        // plain form first and retry as a collection only when the first attempt says "not found".
        var first = await StatCoreAsync(path, asCollection: false, cancellationToken).ConfigureAwait(false);
        if (first.Exists || path.Inner.Trim('/').Length == 0) return first;
        return await StatCoreAsync(path, asCollection: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task<VfsStat> StatCoreAsync(VfsPath path, bool asCollection, CancellationToken cancellationToken)
    {
        var requestPath = BuildUrl(path, asCollection);
        using var request = new HttpRequestMessage(PropFindMethod, requestPath);
        request.Headers.TryAddWithoutValidation("Depth", "0");
        request.Content = new StringContent(PropFindBody, Encoding.UTF8, "application/xml");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return VfsStat.Missing;
        await EnsureSuccessAsync(response, "PROPFIND", requestPath, cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var entries = ParseMultiStatus(body);
        if (entries.Count == 0) return VfsStat.Missing;

        var entry = entries[0];
        return new VfsStat(true, entry.IsDirectory, entry.IsDirectory ? 0 : entry.Size, entry.Modified);
    }

    public async Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var requestPath = BuildUrl(path, asCollection: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestPath);
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await EnsureSuccessAsync(response, "GET", requestPath, cancellationToken).ConfigureAwait(false);
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new RemoteHttpStream(stream, response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public Task<Stream> CreateFileAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var target = BuildUrl(path, asCollection: false);
        return Task.FromResult<Stream>(new RemoteStagedWriteStream(
            (tempFile, ct) => PutFileAsync(target, tempFile, ct), cancellationToken));
    }

    private async Task PutFileAsync(string target, string tempFile, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var content = new StreamContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var request = new HttpRequestMessage(HttpMethod.Put, target) { Content = content };

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, "PUT", target, cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var target = BuildUrl(path, asCollection: true);
        using var request = new HttpRequestMessage(new HttpMethod("MKCOL"), target);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        // 405 means the collection already exists on most servers; treat it as success so the
        // operation is idempotent for the caller.
        if (response.StatusCode is HttpStatusCode.MethodNotAllowed) return;
        await EnsureSuccessAsync(response, "MKCOL", target, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken)
    {
        var target = BuildUrl(path, asCollection: false);
        using var request = new HttpRequestMessage(HttpMethod.Delete, target);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        // WebDAV deletes collections deeply in one call, so "recursive" needs no extra work.
        if (response.StatusCode is HttpStatusCode.NotFound) return;
        await EnsureSuccessAsync(response, "DELETE", target, cancellationToken).ConfigureAwait(false);
    }

    public Task RenameAsync(VfsPath path, string newName, CancellationToken cancellationToken)
    {
        ValidateName(newName);
        var parent = path.Parent ?? throw new IOException("Cannot rename the root of a WebDAV location");
        return MoveCoreAsync(path, parent.Combine(newName), overwrite: false, cancellationToken);
    }

    public Task MoveAsync(VfsPath source, VfsPath target, CancellationToken cancellationToken)
        => MoveCoreAsync(source, target, overwrite: true, cancellationToken);

    private async Task MoveCoreAsync(VfsPath source, VfsPath target, bool overwrite, CancellationToken cancellationToken)
    {
        var from = BuildUrl(source, asCollection: false);
        var to = BuildUrl(target, asCollection: false);
        if (string.Equals(from, to, StringComparison.Ordinal))
            throw new IOException("The source and target are the same item");

        if (!overwrite && await StatAsync(target, cancellationToken).ConfigureAwait(false) is { Exists: true })
            throw new IOException($"An item named '{target.FileName}' already exists");

        using var request = new HttpRequestMessage(new HttpMethod("MOVE"), from);
        request.Headers.TryAddWithoutValidation("Destination", to);
        request.Headers.TryAddWithoutValidation("Overwrite", overwrite ? "T" : "F");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, "MOVE", from, cancellationToken).ConfigureAwait(false);
    }

    private string BuildUrl(VfsPath path, bool asCollection)
    {
        var relative = _location.ToRelativePath(path.Inner);
        var builder = new StringBuilder(_origin);
        if (relative.Length > 0)
        {
            foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                builder.Append(Uri.EscapeDataString(segment));
                builder.Append('/');
            }
        }

        var url = builder.ToString();
        // Only the mount root keeps its trailing slash when a file-style URL is requested;
        // otherwise "GET /a/b/" would hit a collection on strict servers.
        if (!asCollection && url.Length > _origin.Length) url = url.TrimEnd('/');
        return url;
    }

    /// <summary>Relative path of an href inside this mount, or null when it points elsewhere.</summary>
    private string? ToRelativePath(string href)
    {
        string absolutePath;
        if (Uri.TryCreate(href, UriKind.Absolute, out var absolute))
        {
            if (absolute.Scheme != Uri.UriSchemeHttp && absolute.Scheme != Uri.UriSchemeHttps) return null;
            absolutePath = absolute.AbsolutePath;
        }
        else
        {
            var cut = href.IndexOf('?');
            absolutePath = cut < 0 ? href : href[..cut];
        }

        absolutePath = Uri.UnescapeDataString(absolutePath);
        var basePath = Uri.UnescapeDataString(_originPath);
        if (!absolutePath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
        {
            // The href may be rooted at "/" while the mount lives deeper; fall back to matching
            // the mount path anywhere in the href, which is what servers behind a prefix do.
            var index = absolutePath.IndexOf(basePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
            if (index < 0) return null;
            absolutePath = absolutePath[index..];
        }

        return absolutePath[basePath.Length..].Trim('/');
    }

    private static string NormalizePath(string path)
        => Uri.UnescapeDataString(path).TrimEnd('/').ToLowerInvariant();

    private static List<MultiStatusEntry> ParseMultiStatus(string xml)
    {
        var result = new List<MultiStatusEntry>();
        if (string.IsNullOrWhiteSpace(xml)) return result;

        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new IOException("WebDAV returned a malformed multi-status response", ex);
        }

        foreach (var response in document.Descendants().Where(e => e.Name.LocalName == "response"))
        {
            var href = response.Elements().FirstOrDefault(e => e.Name.LocalName == "href")?.Value.Trim();
            if (string.IsNullOrEmpty(href)) continue;

            var propstat = response.Elements()
                .Where(e => e.Name.LocalName == "propstat")
                .FirstOrDefault(e => e.Elements().Any(x =>
                    x.Name.LocalName == "status" &&
                    x.Value.Contains(" 200 ", StringComparison.Ordinal)));
            propstat ??= response.Elements().FirstOrDefault(e => e.Name.LocalName == "propstat");

            var prop = propstat?.Elements().FirstOrDefault(e => e.Name.LocalName == "prop");
            if (prop is null) continue;

            var displayName = prop.Elements().FirstOrDefault(e => e.Name.LocalName == "displayname")?.Value;
            var lengthText = prop.Elements().FirstOrDefault(e => e.Name.LocalName == "getcontentlength")?.Value;
            var modifiedText = prop.Elements().FirstOrDefault(e => e.Name.LocalName == "getlastmodified")?.Value;

            long.TryParse(lengthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
            var modified = DateTimeOffset.TryParse(modifiedText, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : default;

            var resourceType = prop.Elements().FirstOrDefault(e => e.Name.LocalName == "resourcetype");
            var isDirectory = resourceType is not null &&
                              resourceType.Elements().Any(e => e.Name.LocalName == "collection");

            result.Add(new MultiStatusEntry(href, displayName, isDirectory, size, modified));
        }

        return result;
    }

    private async Task EnsureSuccessAsync(
        HttpResponseMessage response, string operation, string url, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        var detail = string.Empty;
        try
        {
            detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or ObjectDisposedException or IOException)
        {
        }
        if (detail.Length > 400) detail = detail[..400];

        var status = (int)response.StatusCode;
        var host = _location.Host;
        var message = status switch
        {
            401 => $"WebDAV 401: credentials rejected for {host}",
            403 => $"WebDAV 403: access denied on {url}",
            404 => $"WebDAV 404: not found: {url}",
            405 => $"WebDAV 405: the server does not allow {operation} on {url}",
            409 => $"WebDAV 409: the parent collection of {url} does not exist",
            412 => $"WebDAV 412: precondition failed for {url}",
            423 => $"WebDAV 423: {url} is locked",
            507 => $"WebDAV 507: not enough storage on {host}",
            _ => $"WebDAV {status} ({response.ReasonPhrase}) on {operation} {url}"
        };

        throw status switch
        {
            401 or 403 => new UnauthorizedAccessException(message),
            404 => new FileNotFoundException(message, url),
            _ => new IOException(string.IsNullOrEmpty(detail) ? message : $"{message}: {detail}")
        };
    }

    private static void ValidateName(string newName)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName.IndexOf('/') >= 0 || newName.IndexOf('\\') >= 0)
            throw new ArgumentException("The new name must be a single path segment", nameof(newName));
    }

    /// <summary>Releases the pooled HTTP connections.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }

    private sealed record MultiStatusEntry(
        string Href, string? DisplayName, bool IsDirectory, long Size, DateTimeOffset Modified);
}
