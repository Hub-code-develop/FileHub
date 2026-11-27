// Copyright (c) 2026 Hub-code-develop. Licensed under the MIT License.
//
// S3 location. No SDK: S3 speaks plain HTTP and the only non-trivial part is Signature V4, which
// is implemented below so the app keeps a small dependency surface. Both AWS and path-style
// compatible servers (MinIO, Ceph, NAS gateways) work; the endpoint is configurable and defaults
// to the regional AWS host.
//
// "Folders" do not exist in S3. A prefix ending in '/' is shown as a folder, a zero-byte object
// whose key ends in '/' is created for an explicit folder, and DeleteObject/Move work on keys.

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using FileHub.Files.Models;

namespace FileHub.Files.Vfs;

public sealed class S3FileSystemProvider : IFileSystemProvider, IDisposable
{
    private const string ServiceName = "s3";

    private readonly RemoteLocation _location;
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _region;
    private readonly string _accessKey;
    private readonly string _secretKey;
    private readonly string _bucket;
    private readonly string _keyPrefix;
    private bool _disposed;

    /// <summary>Creates a provider for one saved S3 bucket.</summary>
    /// <exception cref="InvalidOperationException">Bucket, credentials or endpoint configuration is invalid.</exception>
    public S3FileSystemProvider(RemoteLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (string.IsNullOrWhiteSpace(location.Host))
            throw new InvalidOperationException("An S3 location needs a bucket name in Host");
        if (string.IsNullOrEmpty(location.User) || string.IsNullOrEmpty(location.SecretKey ?? location.Password))
            throw new InvalidOperationException(
                $"S3 credentials for '{location.Name}' are incomplete: an access key and a secret key are required");

        _location = location;
        _bucket = location.Host;
        _region = string.IsNullOrWhiteSpace(location.Region) ? "us-east-1" : location.Region!;
        _accessKey = location.User!;
        _secretKey = (location.SecretKey ?? location.Password)!;
        _keyPrefix = (location.BasePath ?? string.Empty).Replace('\\', '/').Trim('/');

        var endpointText = string.IsNullOrWhiteSpace(location.Endpoint)
            ? $"https://s3.{_region}.amazonaws.com"
            : location.Endpoint!.TrimEnd('/');
        if (!endpointText.Contains("://", StringComparison.Ordinal)) endpointText = "https://" + endpointText;
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException($"The S3 endpoint '{location.Endpoint}' is not a valid HTTP URL");
        _endpoint = endpoint;

        _http = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(20),
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        })
        {
            Timeout = TimeSpan.FromMinutes(30)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("FileHub/1.0");
    }

    /// <summary>The bookmark this provider serves.</summary>
    public RemoteLocation Location => _location;

    /// <inheritdoc/>
    public VfsScheme Scheme => VfsScheme.S3;

    /// <inheritdoc/>
    public string DisplayName => "S3 bucket";

    public async Task<IReadOnlyList<FileEntry>> ListAsync(VfsPath path, bool includeHidden, CancellationToken cancellationToken)
    {
        var listPrefix = BuildListPrefix(path);
        var result = new List<FileEntry>();
        string? continuation = null;

        do
        {
            var query = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["list-type"] = "2",
                ["delimiter"] = "/",
                ["max-keys"] = "1000"
            };
            if (listPrefix.Length > 0) query["prefix"] = listPrefix;
            if (continuation is not null) query["continuation-token"] = continuation;

            var page = await ListPageAsync(query, cancellationToken).ConfigureAwait(false);

            foreach (var folder in page.Prefixes)
            {
                var relative = ToMountRelative(folder).TrimEnd('/');
                if (relative.Length == 0) continue;

                var name = relative[(relative.LastIndexOf('/') + 1)..];
                if (name.Length == 0) continue;

                var isHidden = name.StartsWith('.');
                if (isHidden && !includeHidden) continue;

                var childPath = new VfsPath(VfsScheme.S3, _location.Authority, "/" + relative);
                result.Add(new FileEntry
                {
                    Name = name,
                    FullPath = childPath.Canonical,
                    IsDirectory = true,
                    IsHidden = isHidden,
                    Size = 0,
                    LastWriteTime = default
                });
            }

            foreach (var item in page.Objects)
            {
                // Skip the folder marker object itself ("photos/" while listing "photos").
                if (item.Key.Length == 0 || item.Key == listPrefix) continue;
                if (item.Key.EndsWith('/')) continue;

                var relative = ToMountRelative(item.Key);
                var name = relative[(relative.LastIndexOf('/') + 1)..];
                if (name.Length == 0) continue;

                var isHidden = name.StartsWith('.');
                if (isHidden && !includeHidden) continue;

                var childPath = new VfsPath(VfsScheme.S3, _location.Authority, "/" + relative);
                result.Add(new FileEntry
                {
                    Name = name,
                    FullPath = childPath.Canonical,
                    IsDirectory = false,
                    IsHidden = isHidden,
                    Size = item.Size,
                    LastWriteTime = item.LastModified
                });
            }

            continuation = page.IsTruncated ? page.NextToken : null;
        }
        while (continuation is not null);

        return result;
    }

    public async Task<VfsStat> StatAsync(VfsPath path, CancellationToken cancellationToken)
    {
        if (path.Inner.Trim('/').Length == 0 && _keyPrefix.Length == 0)
        {
            using var bucketResponse = await SendAsync(HttpMethod.Head, string.Empty, null, S3Signer.EmptyPayloadHash,
                null, cancellationToken).ConfigureAwait(false);
            if (bucketResponse.StatusCode is HttpStatusCode.NotFound) return VfsStat.Missing;
            if (!bucketResponse.IsSuccessStatusCode)
                await ThrowForStatusAsync(bucketResponse, "HEAD bucket", "/", cancellationToken).ConfigureAwait(false);
            return new VfsStat(true, true, 0, default);
        }

        var key = BuildKey(path);

        var head = await HeadAsync(key, cancellationToken).ConfigureAwait(false);
        if (head is not null)
        {
            return new VfsStat(true, key.EndsWith('/'), head.Value.Size, head.Value.Modified);
        }

        if (!key.EndsWith('/'))
        {
            var folderHead = await HeadAsync(key + "/", cancellationToken).ConfigureAwait(false);
            if (folderHead is not null)
                return new VfsStat(true, true, 0, folderHead.Value.Modified);
        }

        var probe = await ListPageAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["list-type"] = "2",
            ["prefix"] = key.EndsWith('/') ? key : key + "/",
            ["max-keys"] = "1"
        }, cancellationToken).ConfigureAwait(false);

        return probe.Objects.Count > 0 || probe.Prefixes.Count > 0
            ? new VfsStat(true, true, 0, default)
            : VfsStat.Missing;
    }

    private async Task<ObjectHead?> HeadAsync(string key, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Head, key, null, S3Signer.EmptyPayloadHash,
            null, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
        {
            // A 403 on HeadObject is also what AWS returns for a missing key when the caller has no
            // ListBucket permission, so it must not be silently turned into "missing".
            await ThrowForStatusAsync(response, "HEAD", key, cancellationToken).ConfigureAwait(false);
        }

        var size = response.Content.Headers.ContentLength ?? 0;
        var modified = response.Content.Headers.LastModified ?? response.Headers.Date ?? default;
        return new ObjectHead(size, modified);
    }

    public async Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var key = BuildKey(path);
        var response = await SendAsync(HttpMethod.Get, key, null, S3Signer.EmptyPayloadHash, null, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (!response.IsSuccessStatusCode)
                await ThrowForStatusAsync(response, "GET", key, cancellationToken).ConfigureAwait(false);

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
        var key = BuildKey(path);
        if (key.Length == 0) throw new IOException("Cannot write to the bucket root");
        return Task.FromResult<Stream>(new RemoteStagedWriteStream(
            (tempFile, ct) => PutFileAsync(key, tempFile, ct), cancellationToken));
    }

    private async Task PutFileAsync(string key, string tempFile, CancellationToken cancellationToken)
    {
        // Signature V4 needs the payload hash up front, so the staged file is hashed first and
        // streamed to the wire afterwards.
        string payloadHash;
        await using (var hashStream = new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.Read,
                         64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            payloadHash = S3Signer.Sha256Hex(await SHA256.HashDataAsync(hashStream, cancellationToken).ConfigureAwait(false));
        }

        await using var file = new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var content = new StreamContent(file);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        using var response = await SendAsync(HttpMethod.Put, key, null, payloadHash, null, cancellationToken,
            content).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            await ThrowForStatusAsync(response, "PUT", key, cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var key = BuildKey(path).TrimEnd('/') + "/";
        using var content = new ByteArrayContent([]);
        using var response = await SendAsync(HttpMethod.Put, key, null, S3Signer.EmptyPayloadHash, null,
            cancellationToken, content).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            await ThrowForStatusAsync(response, "PUT folder", key, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken)
    {
        var key = BuildKey(path);
        if (key.Length == 0)
        {
            if (!recursive) throw new IOException("The bucket root is not empty");
            var all = await ListAllKeysAsync(string.Empty, cancellationToken).ConfigureAwait(false);
            foreach (var item in all)
                await DeleteKeyAsync(item, cancellationToken).ConfigureAwait(false);
            return;
        }

        var stat = await StatAsync(path, cancellationToken).ConfigureAwait(false);
        if (!stat.Exists) return;

        if (!stat.IsDirectory)
        {
            await DeleteKeyAsync(key, cancellationToken).ConfigureAwait(false);
            return;
        }

        var prefix = key.EndsWith('/') ? key : key + "/";
        var keys = await ListAllKeysAsync(prefix, cancellationToken).ConfigureAwait(false);

        if (!recursive && keys.Count > 1)
            throw new IOException($"The folder '{path.FileName}' is not empty");

        foreach (var item in keys)
            await DeleteKeyAsync(item, cancellationToken).ConfigureAwait(false);
        if (!keys.Contains(prefix, StringComparer.Ordinal))
            await DeleteKeyAsync(prefix, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteKeyAsync(string key, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Delete, key, null, S3Signer.EmptyPayloadHash,
            null, cancellationToken).ConfigureAwait(false);
        // S3 answers 204 for a delete of a key that never existed; 404 only shows up on odd gateways.
        if (response.StatusCode is HttpStatusCode.NotFound) return;
        if (!response.IsSuccessStatusCode)
            await ThrowForStatusAsync(response, "DELETE", key, cancellationToken).ConfigureAwait(false);
    }

    public async Task RenameAsync(VfsPath path, string newName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName.Contains('/'))
            throw new ArgumentException("The new name must be a single path segment", nameof(newName));

        var parent = path.Parent ?? throw new IOException("Cannot rename the bucket root");
        await MoveAsync(path, parent.Combine(newName), cancellationToken).ConfigureAwait(false);
    }

    public async Task MoveAsync(VfsPath source, VfsPath target, CancellationToken cancellationToken)
    {
        var from = BuildKey(source);
        var to = BuildKey(target);
        if (from.Length == 0 || to.Length == 0) throw new IOException("Cannot move the bucket root");

        var stat = await StatAsync(source, cancellationToken).ConfigureAwait(false);
        if (!stat.Exists) throw new FileNotFoundException($"S3 object not found: {from}", from);

        if (!stat.IsDirectory)
        {
            await CopyKeyAsync(from, to, cancellationToken).ConfigureAwait(false);
            await DeleteKeyAsync(from, cancellationToken).ConfigureAwait(false);
            return;
        }

        // A prefix move is a copy of every key underneath it; there is no folder object to rename.
        var prefix = from.EndsWith('/') ? from : from + "/";
        var targetPrefix = to.EndsWith('/') ? to : to + "/";
        var keys = await ListAllKeysAsync(prefix, cancellationToken).ConfigureAwait(false);
        foreach (var key in keys)
        {
            var destination = targetPrefix + key[prefix.Length..];
            await CopyKeyAsync(key, destination, cancellationToken).ConfigureAwait(false);
        }
        foreach (var key in keys)
            await DeleteKeyAsync(key, cancellationToken).ConfigureAwait(false);
    }

    private async Task CopyKeyAsync(string fromKey, string toKey, CancellationToken cancellationToken)
    {
        var copySource = "/" + S3Signer.Encode(_bucket) + "/" + EncodeKeyPath(fromKey);
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["x-amz-copy-source"] = copySource
        };

        using var response = await SendAsync(HttpMethod.Put, toKey, null, S3Signer.EmptyPayloadHash,
            headers, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            await ThrowForStatusAsync(response, "COPY", $"{fromKey} -> {toKey}", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lists every key under a prefix, following continuation tokens.</summary>
    private async Task<List<string>> ListAllKeysAsync(string prefix, CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        string? continuation = null;
        do
        {
            var query = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["list-type"] = "2",
                ["max-keys"] = "1000"
            };
            if (prefix.Length > 0) query["prefix"] = prefix;
            if (continuation is not null) query["continuation-token"] = continuation;

            var page = await ListPageAsync(query, cancellationToken).ConfigureAwait(false);
            foreach (var item in page.Objects) keys.Add(item.Key);
            continuation = page.IsTruncated ? page.NextToken : null;
        }
        while (continuation is not null);

        return keys;
    }

    private async Task<ListPage> ListPageAsync(Dictionary<string, string> query, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, string.Empty, query, S3Signer.EmptyPayloadHash,
            null, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            await ThrowForStatusAsync(response, "LIST", "/" + _bucket, cancellationToken).ConfigureAwait(false);

        var xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseListPage(xml);
    }

    private static ListPage ParseListPage(string xml)
    {
        var objects = new List<ListedObject>();
        var prefixes = new List<string>();
        var truncated = false;
        string? token = null;

        if (string.IsNullOrWhiteSpace(xml)) return new ListPage(objects, prefixes, false, null);

        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new IOException("The S3 endpoint returned a malformed ListObjectsV2 response", ex);
        }

        foreach (var element in document.Descendants())
        {
            switch (element.Name.LocalName)
            {
                case "Contents":
                {
                    var keyElement = element.Elements().FirstOrDefault(e => e.Name.LocalName == "Key");
                    if (keyElement is null) break;
                    var sizeText = element.Elements().FirstOrDefault(e => e.Name.LocalName == "Size")?.Value;
                    var modifiedText = element.Elements().FirstOrDefault(e => e.Name.LocalName == "LastModified")?.Value;
                    long.TryParse(sizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
                    var modified = DateTimeOffset.TryParse(modifiedText, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                        ? parsed
                        : default;
                    objects.Add(new ListedObject(keyElement.Value, size, modified));
                    break;
                }
                case "CommonPrefixes":
                {
                    var prefixElement = element.Elements().FirstOrDefault(e => e.Name.LocalName == "Prefix");
                    if (prefixElement is not null) prefixes.Add(prefixElement.Value);
                    break;
                }
                case "IsTruncated":
                    truncated = string.Equals(element.Value, "true", StringComparison.OrdinalIgnoreCase);
                    break;
                case "NextContinuationToken":
                    token = element.Value;
                    break;
            }
        }

        return new ListPage(objects, prefixes, truncated, token);
    }

    /// <summary>Builds and sends a signed request. The body, when present, must already be hashed.</summary>
    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string key,
        IReadOnlyDictionary<string, string>? query,
        string payloadHash,
        IReadOnlyDictionary<string, string>? extraHeaders,
        CancellationToken cancellationToken,
        HttpContent? content = null)
    {
        var signed = S3Signer.Sign(method.Method, _endpoint, _bucket, key, query, extraHeaders,
            payloadHash, _region, _accessKey, _secretKey, DateTimeOffset.UtcNow);

        using var request = new HttpRequestMessage(method, signed.Url);
        request.Headers.Host = signed.HostHeader;
        foreach (var header in signed.Headers)
        {
            if (header.Key is "host" or "x-amz-content-sha256" or "x-amz-date") continue;
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", signed.PayloadHash);
        request.Headers.TryAddWithoutValidation("x-amz-date", signed.AmzDate);
        request.Headers.TryAddWithoutValidation("Authorization", signed.Authorization);
        request.Content = content;

        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ThrowForStatusAsync(
        HttpResponseMessage response, string operation, string target, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        var code = string.Empty;
        var detail = string.Empty;
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    var document = XDocument.Parse(body);
                    code = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "Code")?.Value ?? string.Empty;
                    detail = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "Message")?.Value ?? string.Empty;
                }
                catch (System.Xml.XmlException)
                {
                    detail = body.Length > 300 ? body[..300] : body;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or ObjectDisposedException or IOException)
        {
        }

        var label = $"S3 {status}" + (string.IsNullOrEmpty(code) ? string.Empty : $" {code}");
        var message = status switch
        {
            301 or 400 when code == "AuthorizationHeaderMalformed" =>
                $"{label}: the endpoint expects a different region than '{_region}'",
            401 or 403 => $"{label}: credentials rejected or permission denied for {operation} {target}",
            404 => $"{label}: not found: {target}",
            409 => $"{label}: conflicting operation on {target}",
            _ => $"{label} on {operation} {target}"
        };
        if (!string.IsNullOrEmpty(detail)) message += ": " + detail;

        throw status switch
        {
            401 or 403 => new UnauthorizedAccessException(message),
            404 => new FileNotFoundException(message, target),
            _ => new IOException(message)
        };
    }

    private string BuildKey(VfsPath path) => _location.ToRelativePath(path.Inner);

    /// <summary>Strips the bookmarked key prefix so entries carry mount-relative virtual paths.</summary>
    private string ToMountRelative(string key)
    {
        if (_keyPrefix.Length == 0) return key;
        if (key.Length == _keyPrefix.Length && string.Equals(key, _keyPrefix, StringComparison.Ordinal))
            return string.Empty;
        if (key.StartsWith(_keyPrefix + "/", StringComparison.Ordinal))
            return key[(_keyPrefix.Length + 1)..];
        return key;
    }

    private string BuildListPrefix(VfsPath path)
    {
        var key = BuildKey(path);
        if (key.Length == 0) return string.Empty;
        return key.EndsWith('/') ? key : key + "/";
    }

    private static string EncodeKeyPath(string key)
        => string.Join('/', key.Split('/').Select(segment => S3Signer.Encode(segment)));

    /// <summary>Releases the pooled HTTP connections.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }

    private sealed record ListedObject(string Key, long Size, DateTimeOffset LastModified);

    private sealed record ListPage(
        IReadOnlyList<ListedObject> Objects, IReadOnlyList<string> Prefixes, bool IsTruncated, string? NextToken);

    private readonly record struct ObjectHead(long Size, DateTimeOffset Modified);
}

/// <summary>
/// Result of signing a request. <see cref="CanonicalRequest"/> and <see cref="StringToSign"/> are
/// exposed so the format can be checked without a live bucket.
/// </summary>
public sealed record S3SignedRequest(
    string Url,
    string HostHeader,
    string AmzDate,
    string PayloadHash,
    string Authorization,
    IReadOnlyDictionary<string, string> Headers,
    string CanonicalRequest,
    string StringToSign);

/// <summary>
/// AWS Signature V4 for the S3 service, implemented directly from the specification.
/// </summary>
public static class S3Signer
{
    /// <summary>SHA-256 of an empty body; mandatory for GET/HEAD/DELETE.</summary>
    public const string EmptyPayloadHash =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>RFC 3986 percent-encoding, uppercase hex, '/' optionally preserved.</summary>
    public static string Encode(string value, bool encodeSlash = true)
    {
        var builder = new StringBuilder(value.Length * 3);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~' || (!encodeSlash && c == '/'))
                builder.Append(c);
            else
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    /// <summary>Lowercase hex SHA-256 digest.</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> data)
        => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>Sorted, encoded query string; a null value becomes "key=".</summary>
    public static string CanonicalQuery(IReadOnlyDictionary<string, string>? query)
    {
        if (query is null || query.Count == 0) return string.Empty;
        return string.Join('&', query
            .OrderBy(kv => Encode(kv.Key), StringComparer.Ordinal)
            .Select(kv => $"{Encode(kv.Key)}={Encode(kv.Value ?? string.Empty)}"));
    }

    /// <summary>
    /// The canonical request: method, URI, query, canonical headers, signed header list, payload hash.
    /// Each part is separated by a newline and every header line ends with one.
    /// </summary>
    public static string BuildCanonicalRequest(
        string method, string canonicalUri, string canonicalQuery,
        string canonicalHeaders, string signedHeaders, string payloadHash)
        => string.Join('\n',
        [
            method.ToUpperInvariant(),
            canonicalUri,
            canonicalQuery,
            canonicalHeaders,
            signedHeaders,
            payloadHash
        ]);

    /// <summary>StringToSign: algorithm, timestamp, scope, hash of the canonical request.</summary>
    public static string BuildStringToSign(string amzDate, string scope, string canonicalRequest)
        => string.Join('\n',
        [
            "AWS4-HMAC-SHA256",
            amzDate,
            scope,
            Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest))
        ]);

    /// <summary>Derives the date/region/service scoped signing key.</summary>
    public static byte[] DeriveSigningKey(string secretKey, string dateStamp, string region, string service)
    {
        var kDate = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + secretKey), dateStamp);
        var kRegion = HmacSha256(kDate, region);
        var kService = HmacSha256(kRegion, service);
        return HmacSha256(kService, "aws4_request");
    }

    /// <summary>Hex HMAC-SHA256 of a UTF-8 string.</summary>
    public static string HmacSha256Hex(byte[] key, string data)
        => Convert.ToHexString(HmacSha256(key, data)).ToLowerInvariant();

    private static byte[] HmacSha256(byte[] key, string data)
        => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    /// <summary>
    /// Signs one S3 request. <paramref name="key"/> is the object key relative to the bucket and
    /// may be empty for bucket-level calls (ListObjectsV2).
    /// </summary>
    public static S3SignedRequest Sign(
        string method,
        Uri endpoint,
        string bucket,
        string key,
        IReadOnlyDictionary<string, string>? query,
        IReadOnlyDictionary<string, string>? extraHeaders,
        string payloadHash,
        string region,
        string accessKey,
        string secretKey,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var dateStamp = now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var scope = $"{dateStamp}/{region}/s3/aws4_request";
        var host = endpoint.IsDefaultPort ? endpoint.Host : $"{endpoint.Host}:{endpoint.Port}";

        var pathPrefix = endpoint.AbsolutePath.TrimEnd('/');
        var canonicalUri = pathPrefix + "/" + Encode(bucket);
        if (key.Length > 0) canonicalUri += "/" + string.Join('/', key.Split('/').Select(segment => Encode(segment)));

        var canonicalQuery = CanonicalQuery(query);

        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = host,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = amzDate
        };
        if (extraHeaders is not null)
        {
            foreach (var header in extraHeaders)
                headers[header.Key.ToLowerInvariant()] = header.Value.Trim();
        }

        var signedHeaderList = string.Join(';', headers.Keys);
        var canonicalHeaders = string.Concat(headers.Select(kv => $"{kv.Key}:{kv.Value}\n"));

        var canonicalRequest = BuildCanonicalRequest(method, canonicalUri, canonicalQuery,
            canonicalHeaders, signedHeaderList, payloadHash);
        var stringToSign = BuildStringToSign(amzDate, scope, canonicalRequest);
        var signingKey = DeriveSigningKey(secretKey, dateStamp, region, "s3");
        var signature = HmacSha256Hex(signingKey, stringToSign);

        var authorization =
            $"AWS4-HMAC-SHA256 Credential={accessKey}/{scope}, SignedHeaders={signedHeaderList}, Signature={signature}";

        var url = $"{endpoint.Scheme}://{host}{canonicalUri}" +
                  (canonicalQuery.Length > 0 ? "?" + canonicalQuery : string.Empty);

        return new S3SignedRequest(url, host, amzDate, payloadHash, authorization, headers,
            canonicalRequest, stringToSign);
    }
}
