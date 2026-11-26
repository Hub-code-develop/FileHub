// Visual Studio Marketplace Gallery client.
//
// Uses the public gallery query endpoint that VS Code itself talks to. No extension package is
// bundled with FileHub: packages are downloaded to a temporary file only when the user asks for
// an install, then extracted into FileHub's own extension root so VS Code's metadata is untouched.
//
// "Visual Studio Marketplace" and "Visual Studio Code" are trademarks of Microsoft Corporation.
// FileHub is not affiliated with, endorsed by, or an official client of Microsoft.

using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FileHub.Code.Models;

namespace FileHub.Code.Services;

/// <summary>Searches the Visual Studio Marketplace and installs VSIX packages for local use.</summary>
public sealed class MarketplaceService
{
    private const string Endpoint =
        "https://marketplace.visualstudio.com/_apis/public/gallery/extensionquery";
    private const string AcceptHeader = "application/json;api-version=7.2-preview.1";
    private const string Target = "Microsoft.VisualStudio.Code";

    // IncludeVersions | IncludeFiles | IncludeVersionProperties | IncludeStatistics |
    // IncludeLatestVersionOnly-free variant so platform-specific versions stay visible.
    private const int QueryFlags = 950;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        // Identify honestly: never masquerade as the official VS Code client.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FileHub/1.0 (+marketplace client; MIT)");
        client.DefaultRequestHeaders.Add("X-Market-Client-Id", "FileHub-1.0");
        return client;
    }

    /// <summary>Marketplace target platform token for the current OS / architecture.</summary>
    public static string CurrentPlatform
    {
        get
        {
            var arch = RuntimeInformation.ProcessArchitecture;
            var suffix = arch switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X86 => "x86",
                _ => "x64"
            };
            if (OperatingSystem.IsWindows()) return "win32-" + suffix;
            if (OperatingSystem.IsMacOS()) return "darwin-" + suffix;
            if (OperatingSystem.IsLinux()) return "linux-" + suffix;
            return "universal";
        }
    }

    /// <summary>Where FileHub extracts installed VSIX packages (kept separate from VS Code).</summary>
    public static string InstallRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileHub", "extensions");

    /// <summary>Queries the gallery. An empty query returns a curated popular set.</summary>
    public async Task<IReadOnlyList<MarketplaceExtension>> SearchAsync(
        string query, int pageSize = 30, CancellationToken cancellationToken = default)
    {
        var searchText = string.IsNullOrWhiteSpace(query) ? "extension" : query.Trim();
        var body = new
        {
            filters = new[]
            {
                new
                {
                    criteria = new object[]
                    {
                        new { filterType = 8, value = Target },
                        new { filterType = 10, value = searchText },
                        new { filterType = 12, value = "4096" }   // exclude unpublished
                    },
                    pageNumber = 1,
                    pageSize = Math.Clamp(pageSize, 1, 100),
                    sortBy = 0,
                    sortOrder = 0
                }
            },
            flags = QueryFlags
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd(AcceptHeader);

        using var response = await Http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var result = new List<MarketplaceExtension>();
        if (!document.RootElement.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array) return result;

        foreach (var group in results.EnumerateArray())
        {
            if (!group.TryGetProperty("extensions", out var extensions) ||
                extensions.ValueKind != JsonValueKind.Array) continue;
            foreach (var element in extensions.EnumerateArray())
            {
                var parsed = Parse(element);
                if (parsed is not null) result.Add(parsed);
            }
        }
        return result;
    }

    private static MarketplaceExtension? Parse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        var name = ReadString(element, "extensionName");
        if (!element.TryGetProperty("publisher", out var publisher)) return null;
        var publisherName = ReadString(publisher, "publisherName");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(publisherName)) return null;

        var versions = element.TryGetProperty("versions", out var v) && v.ValueKind == JsonValueKind.Array
            ? v : default;
        var selected = SelectVersion(versions, out var platform, out var compatible);

        var statistics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (element.TryGetProperty("statistics", out var stats) && stats.ValueKind == JsonValueKind.Array)
        {
            foreach (var stat in stats.EnumerateArray())
            {
                var key = ReadString(stat, "statisticName");
                if (key.Length > 0 && stat.TryGetProperty("value", out var value) &&
                    value.ValueKind == JsonValueKind.Number)
                    statistics[key] = value.GetDouble();
            }
        }

        return new MarketplaceExtension
        {
            Id = publisherName + "." + name,
            Name = name,
            Publisher = publisherName,
            PublisherDisplayName = ReadString(publisher, "displayName"),
            DisplayName = ReadString(element, "displayName"),
            Description = ReadString(element, "shortDescription"),
            Version = selected is null ? string.Empty : ReadString(selected.Value, "version"),
            TargetPlatform = platform,
            IconUrl = FindAsset(element, "Microsoft.VisualStudio.Services.Icons.Default"),
            LicenseUrl = FindAsset(element, "Microsoft.VisualStudio.Services.Content.License"),
            VsixUrl = selected is null ? string.Empty
                : FindAsset(selected.Value, "Microsoft.VisualStudio.Services.VSIXPackage"),
            InstallCount = statistics.TryGetValue("install", out var install) ? (long)install : 0,
            Rating = statistics.TryGetValue("averagerating", out var rating) ? rating : 0,
            RatingCount = statistics.TryGetValue("ratingcount", out var count) ? (long)count : 0,
            IsVerifiedPublisher = ReadString(publisher, "flags").Contains("verified", StringComparison.OrdinalIgnoreCase),
            IsCompatible = compatible
        };
    }

    /// <summary>Picks the newest version whose package matches this platform.</summary>
    private static JsonElement? SelectVersion(JsonElement versions, out string platform, out bool compatible)
    {
        platform = string.Empty;
        compatible = false;
        if (versions.ValueKind != JsonValueKind.Array) return null;

        JsonElement? fallback = null;
        foreach (var version in versions.EnumerateArray())
        {
            if (version.ValueKind != JsonValueKind.Object) continue;
            fallback ??= version;
            var target = ReadString(version, "targetPlatform");
            // A missing targetPlatform means a platform-neutral (universal) package.
            if (target.Length == 0 || string.Equals(target, CurrentPlatform, StringComparison.OrdinalIgnoreCase))
            {
                platform = target;
                compatible = true;
                return version;
            }
        }
        return fallback;
    }

    private static string FindAsset(JsonElement owner, string assetType)
    {
        var versions = owner.ValueKind == JsonValueKind.Object &&
                       owner.TryGetProperty("versions", out var v) && v.ValueKind == JsonValueKind.Array
            ? v : default;
        var container = versions.ValueKind == JsonValueKind.Array && versions.GetArrayLength() > 0
            ? versions[0] : owner;
        if (container.ValueKind == JsonValueKind.Object &&
            container.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in files.EnumerateArray())
            {
                if (string.Equals(ReadString(file, "assetType"), assetType, StringComparison.OrdinalIgnoreCase))
                    return ReadString(file, "source");
            }
        }
        return string.Empty;
    }

    private static string ReadString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>Downloads a VSIX to a temporary file; the caller owns cleanup.</summary>
    public async Task<string> DownloadAsync(MarketplaceExtension extension,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(extension.VsixUrl))
            throw new InvalidOperationException("This extension has no VSIX package for the current platform.");

        var temp = Path.Combine(Path.GetTempPath(), "filehub-" + Guid.NewGuid().ToString("N") + ".vsix");
        using var response = await Http.GetAsync(extension.VsixUrl, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, useAsync: true);
        var buffer = new byte[81920];
        long read = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            read += count;
            if (total > 0) progress?.Report((double)read / total);
        }
        progress?.Report(1);
        return temp;
    }

    /// <summary>
    /// Extracts a downloaded VSIX into FileHub's own extension root. VS Code packages keep their
    /// manifest under <c>extension/package.json</c>; that prefix is stripped on extraction.
    /// </summary>
    public static string Install(string vsixPath, MarketplaceExtension extension)
    {
        var folderName = extension.Id + "-" + extension.Version;
        foreach (var invalid in Path.GetInvalidFileNameChars())
            folderName = folderName.Replace(invalid, '_');
        var destination = Path.Combine(InstallRoot, folderName);
        var staging = destination + ".staging-" + Guid.NewGuid().ToString("N");

        try
        {
            Directory.CreateDirectory(staging);
            using var archive = ZipFile.OpenRead(vsixPath);
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (name.StartsWith('/') || name.Contains("..")) continue;   // zip-slip guard
                if (name.StartsWith("extension/", StringComparison.OrdinalIgnoreCase))
                    name = name["extension/".Length..];
                if (string.IsNullOrWhiteSpace(name) || name.EndsWith('/')) continue;

                var output = Path.GetFullPath(Path.Combine(staging, name));
                if (!output.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                entry.ExtractToFile(output, overwrite: true);
            }

            if (!File.Exists(Path.Combine(staging, "package.json")))
                throw new InvalidDataException("The VSIX does not contain a valid extension manifest.");

            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            Directory.Move(staging, destination);
            return destination;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>Removes an extension installed by FileHub. Returns whether anything was removed.</summary>
    public static bool Uninstall(string extensionId)
    {
        if (!Directory.Exists(InstallRoot)) return false;
        var prefix = extensionId + "-";
        var removed = false;
        foreach (var folder in Directory.EnumerateDirectories(InstallRoot))
        {
            if (!Path.GetFileName(folder).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.Delete(folder, recursive: true);
            removed = true;
        }
        return removed;
    }
}
