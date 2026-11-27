// Copyright (c) 2026 Hub-code-develop. Licensed under the MIT License.
//
// Turning what a user typed into the address bar into something FileHub can show.
//
//   https://example.com/files/         -> a folder (WebDAV or an HTML index)
//   https://example.com/report.pdf     -> a file, downloaded and opened
//   https://example.com/about          -> a web page, handed to the system browser
//
// The distinction matters: a plain web page has no listing, and pretending it is a folder
// only produces an empty window.

using FileHub.Files.Vfs;

namespace FileHub.Files.Services;

public enum WebLocationKind
{
    /// <summary>A browsable folder: WebDAV collection or an HTML directory index.</summary>
    Folder,

    /// <summary>A single resource that should be downloaded.</summary>
    File,

    /// <summary>An ordinary HTML page, which belongs in a browser.</summary>
    WebPage
}

/// <summary>The result of resolving a typed URL.</summary>
public sealed record WebLocation(
    string Url,
    WebLocationKind Kind,
    string BrowsablePath,
    string? ContentType,
    long Size,
    string? SuggestedName);

public static class WebLocationService
{
    private static readonly HashSet<string> PageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".htm", ".html", ".xhtml", ".shtml", ".php", ".asp", ".aspx", ".jsp", ".cgi", ".do"
    };

    /// <summary>
    /// Recognises a URL in address-bar input. `www.` alone is upgraded to https; anything that
    /// is not http(s) is left to the filesystem. Credentials in the URL are moved into the
    /// provider's credential table so they never appear in the breadcrumb.
    /// </summary>
    public static bool TryParse(string? text, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var candidate = text.Trim().Trim('"');
        if (candidate.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            candidate = "https://" + candidate;

        if (!candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https")) return false;
        if (string.IsNullOrWhiteSpace(uri.Host)) return false;

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var split = uri.UserInfo.Split(':', 2);
            var user = Uri.UnescapeDataString(split[0]);
            var password = split.Length > 1 ? Uri.UnescapeDataString(split[1]) : string.Empty;
            var authority = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
            HttpFileSystemProvider.SetCredentials(authority, user, password);

            var stripped = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty };
            uri = stripped.Uri;
        }

        url = uri.AbsoluteUri;
        return true;
    }

    /// <summary>
    /// Probes the URL once and decides whether to browse, download or hand it to the browser.
    /// Throws a readable exception when the server cannot be reached or answers with an error.
    /// </summary>
    public static async Task<WebLocation> ResolveAsync(string url, CancellationToken cancellationToken = default)
    {
        var uri = new Uri(url);
        var scheme = uri.Scheme == Uri.UriSchemeHttps ? VfsScheme.Https : VfsScheme.Http;
        var provider = new HttpFileSystemProvider(scheme);

        var resource = await provider.DescribeAsync(url, cancellationToken).ConfigureAwait(false);
        if (!resource.Exists)
            throw new FileNotFoundException($"The server has nothing at {url} (HTTP 404).");

        var finalUrl = resource.FinalUrl;
        if (resource.IsDirectory)
            return new WebLocation(finalUrl, WebLocationKind.Folder, WithTrailingSlash(finalUrl),
                resource.ContentType, 0, null);

        var mediaType = resource.ContentType ?? string.Empty;
        var isHtml = mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
                     || mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
        var extension = Path.GetExtension(uri.AbsolutePath);

        // A page is only a page when the server says so and the path does not name a document.
        if (isHtml && (extension.Length == 0 || PageExtensions.Contains(extension)))
            return new WebLocation(finalUrl, WebLocationKind.WebPage, finalUrl, mediaType, 0, null);

        var name = resource.SuggestedName;
        if (string.IsNullOrWhiteSpace(name))
        {
            var last = uri.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault();
            name = string.IsNullOrWhiteSpace(last) ? uri.Host : Uri.UnescapeDataString(last);
        }

        // No content type and no extension: typically an opaque download endpoint. Anything the
        // server calls octet-stream is a file even without an extension.
        if (mediaType.Length == 0 && !Path.HasExtension(name))
            return new WebLocation(finalUrl, WebLocationKind.WebPage, finalUrl, mediaType, 0, null);

        return new WebLocation(finalUrl, WebLocationKind.File, finalUrl, mediaType, resource.Size, name);
    }

    private static string WithTrailingSlash(string url) => url.EndsWith('/') ? url : url + "/";
}
