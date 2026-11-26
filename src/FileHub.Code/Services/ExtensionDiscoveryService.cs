using System.Text.Json;
using FileHub.Code.Models;

namespace FileHub.Code.Services;

/// <summary>
/// Reads VS Code extension manifests. FileHub does not implement the VS Code extension host: it
/// cannot execute extension JavaScript. It does consume the declarative parts Monaco understands
/// (language ids / file associations, colour themes and snippets), which is what the editor page
/// and the Extensions page expose.
/// </summary>
public sealed class ExtensionDiscoveryService
{
    /// <summary>Override for tests and portable installs; separate entries with a semicolon.</summary>
    public const string RootsEnvironmentVariable = "FILEHUB_VSCODE_EXTENSIONS";

    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FileHub", "disabled-extensions.json");

    private HashSet<string> _disabled = new(StringComparer.OrdinalIgnoreCase);

    public ExtensionDiscoveryService()
    {
        LoadDisabled();
    }

    /// <summary>Absolute roots that were actually scanned (useful in the Extensions page).</summary>
    public IReadOnlyList<string> ScannedRoots { get; private set; } = [];

    public IReadOnlyList<ExtensionInfo> Extensions { get; private set; } = [];

    /// <summary>Enabled extensions only, keyed by language file extension including the dot.</summary>
    public IReadOnlyDictionary<string, string> LanguageExtensions { get; private set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public void Refresh()
    {
        LoadDisabled();
        var roots = GetRoots().Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        ScannedRoots = roots;

        var result = new List<ExtensionInfo>();
        foreach (var root in roots)
        {
            foreach (var folder in SafeEnumerateDirectories(root))
            {
                var manifest = Path.Combine(folder, "package.json");
                if (!File.Exists(manifest)) continue;
                try
                {
                    var extension = Parse(manifest, root);
                    if (extension is not null) result.Add(extension);
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    // One broken manifest must not hide every other extension.
                }
            }
        }

        result.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
        foreach (var item in result) item.Enabled = !_disabled.Contains(item.Id);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var extension in result.Where(x => x.Enabled))
        {
            foreach (var language in extension.Languages)
            {
                foreach (var ext in language.Extensions)
                {
                    var normalized = ext.StartsWith('.') ? ext : "." + ext;
                    map[normalized] = language.Id;
                }
            }
        }
        LanguageExtensions = map;
        Extensions = result;
    }

    public void SetEnabled(string extensionId, bool enabled)
    {
        if (enabled) _disabled.Remove(extensionId);
        else _disabled.Add(extensionId);
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_disabled.OrderBy(x => x).ToArray()));
        Refresh();
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root)
    {
        try { return Directory.EnumerateDirectories(root).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static string[] GetRoots()
    {
        var overrideValue = Environment.GetEnvironmentVariable(RootsEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overrideValue))
            return overrideValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new List<string>
        {
            MarketplaceService.InstallRoot, // extensions installed from the marketplace inside FileHub
            Path.Combine(home, ".vscode", "extensions"),
            Path.Combine(home, ".vscode-insiders", "extensions"),
            Path.Combine(home, ".vscode-oss", "extensions"),
            Path.Combine(home, ".cursor", "extensions"),
            Path.Combine(home, ".windsurf", "extensions")
        };
        if (!string.IsNullOrEmpty(localAppData))
        {
            candidates.Add(Path.Combine(localAppData, "Programs", "Microsoft VS Code", "resources", "app", "extensions"));
        }
        return candidates.ToArray();
    }

    private void LoadDisabled()
    {
        try
        {
            if (File.Exists(_settingsPath))
                _disabled = JsonSerializer.Deserialize<string[]>(File.ReadAllText(_settingsPath))?
                    .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _disabled = [];
        }
    }

    private static ExtensionInfo? Parse(string manifestPath, string root)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var rootJson = doc.RootElement;
        if (rootJson.ValueKind != JsonValueKind.Object) return null;

        var name = ReadString(rootJson, "name");
        if (string.IsNullOrWhiteSpace(name)) return null;

        var contributes = rootJson.TryGetProperty("contributes", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;

        return new ExtensionInfo
        {
            Id = name,
            DisplayName = ReadString(rootJson, "displayName"),
            Description = ReadString(rootJson, "description"),
            Version = ReadString(rootJson, "version"),
            Publisher = ReadPublisher(rootJson),
            Folder = Path.GetDirectoryName(manifestPath)!,
            SourceRoot = root,
            Languages = ReadLanguages(contributes),
            Grammars = ReadGrammars(contributes),
            Themes = ReadThemes(contributes, manifestPath),
            Snippets = ReadSnippets(contributes, manifestPath),
            Commands = ReadCommands(contributes)
        };
    }

    private static string ReadString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static string ReadPublisher(JsonElement obj)
    {
        if (!obj.TryGetProperty("publisher", out var p)) return string.Empty;
        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString() ?? string.Empty,
            JsonValueKind.Object => ReadString(p, "name"),
            _ => string.Empty
        };
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
            return [];
        return array.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()!).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
    }

    private static IReadOnlyList<ExtensionLanguage> ReadLanguages(JsonElement contributes)
    {
        if (contributes.ValueKind != JsonValueKind.Object ||
            !contributes.TryGetProperty("languages", out var array) || array.ValueKind != JsonValueKind.Array) return [];
        var result = new List<ExtensionLanguage>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = ReadString(item, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            result.Add(new ExtensionLanguage(id, ReadStringArray(item, "extensions"), ReadStringArray(item, "aliases")));
        }
        return result;
    }

    private static IReadOnlyList<ExtensionGrammar> ReadGrammars(JsonElement contributes)
    {
        if (contributes.ValueKind != JsonValueKind.Object ||
            !contributes.TryGetProperty("grammars", out var array) || array.ValueKind != JsonValueKind.Array) return [];
        var result = new List<ExtensionGrammar>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var path = ReadString(item, "path");
            if (string.IsNullOrWhiteSpace(path)) continue;
            result.Add(new ExtensionGrammar(ReadString(item, "language"), ReadString(item, "scopeName"), path));
        }
        return result;
    }

    private static IReadOnlyList<ExtensionTheme> ReadThemes(JsonElement contributes, string manifestPath)
    {
        if (contributes.ValueKind != JsonValueKind.Object ||
            !contributes.TryGetProperty("themes", out var array) || array.ValueKind != JsonValueKind.Array) return [];
        var result = new List<ExtensionTheme>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var rel = ReadString(item, "path");
            if (string.IsNullOrWhiteSpace(rel)) continue;
            var full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifestPath)!, rel.TrimStart('.', '/')));
            if (!File.Exists(full)) continue;
            result.Add(new ExtensionTheme(ReadString(item, "id"), ReadString(item, "label"), full, ReadString(item, "uiTheme")));
        }
        return result;
    }

    private static IReadOnlyList<ExtensionSnippet> ReadSnippets(JsonElement contributes, string manifestPath)
    {
        if (contributes.ValueKind != JsonValueKind.Object ||
            !contributes.TryGetProperty("snippets", out var array) || array.ValueKind != JsonValueKind.Array) return [];
        var result = new List<ExtensionSnippet>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var rel = ReadString(item, "path");
            if (string.IsNullOrWhiteSpace(rel)) continue;
            var full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifestPath)!, rel.TrimStart('.', '/')));
            if (!File.Exists(full)) continue;
            result.Add(new ExtensionSnippet(ReadString(item, "language"), full));
        }
        return result;
    }

    private static IReadOnlyList<string> ReadCommands(JsonElement contributes)
    {
        if (contributes.ValueKind != JsonValueKind.Object ||
            !contributes.TryGetProperty("commands", out var array) || array.ValueKind != JsonValueKind.Array) return [];
        var result = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object) result.Add(ReadString(item, "title"));
        }
        return result.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
    }
}
