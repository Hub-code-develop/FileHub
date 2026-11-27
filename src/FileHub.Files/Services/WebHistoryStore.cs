// Copyright (c) 2026 Hub-code-develop. Licensed under the MIT License.
//
// A short list of the web locations the user opened from the address bar, so the sidebar can
// offer them back like any other location. Only the URL and a label are stored - never content,
// never credentials.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileHub.Files.Services;

public sealed record WebHistoryItem(string Url, string Title, DateTimeOffset Visited);

public static class WebHistoryStore
{
    private const int MaxItems = 20;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Path of the JSON file, under the per-user application data folder.</summary>
    public static string ConfigPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileHub", "web-history.json");

    public static IReadOnlyList<WebHistoryItem> Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return [];
            var items = JsonSerializer.Deserialize<List<WebHistoryItem>>(File.ReadAllText(ConfigPath), Json);
            return items is null ? [] : items.Where(x => !string.IsNullOrWhiteSpace(x.Url)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>Adds or moves a URL to the front of the list.</summary>
    public static void Record(string url, string? title = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var items = Load().Where(x => !string.Equals(x.Url, url, StringComparison.OrdinalIgnoreCase)).ToList();
        items.Insert(0, new WebHistoryItem(url, title ?? Label(url), DateTimeOffset.Now));
        Save(items.Take(MaxItems));
    }

    public static void Remove(string url)
    {
        var items = Load().Where(x => !string.Equals(x.Url, url, StringComparison.OrdinalIgnoreCase));
        Save(items);
    }

    public static void Clear() => Save([]);

    private static void Save(IEnumerable<WebHistoryItem> items)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            var temp = ConfigPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(items.ToList(), Json));
            File.Move(temp, ConfigPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // History is a convenience; a read-only profile must not break navigation.
        }
    }

    private static string Label(string url)
    {
        try
        {
            var uri = new Uri(url);
            var path = uri.AbsolutePath.Trim('/');
            return path.Length == 0 ? uri.Host : $"{uri.Host} / {path}";
        }
        catch (UriFormatException)
        {
            return url;
        }
    }
}
