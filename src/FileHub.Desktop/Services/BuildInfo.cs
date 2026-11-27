using System.Text.Json;

namespace FileHub.Desktop.Services;

/// <summary>
/// Build-channel metadata baked into the assembly by CI.
///
/// The publish workflows overwrite Assets/version-ci.json before compiling, which is embedded as
/// a resource. Local builds keep the committed "dev / local-build" values. This mirrors MChub's
/// AppVersionService pattern so a download can be identified as stable / nightly / preview at a
/// glance, without needing a separate config file next to the binary.
/// </summary>
public static class BuildInfo
{
    private const string ResourceName = "FileHub.Desktop.Assets.version-ci.json";

    private static readonly BuildInfoData Data = Load();

    public static string Channel => Data.Type;
    public static string Version => Data.Version;
    public static string VersionTitle => Data.VersionTitle;
    public static string Commit => Data.Commit;
    public static string BuildTime => Data.BuildTime;
    public static string RunId => Data.Action;

    /// <summary>Short label shown at the bottom of the sidebar.</summary>
    public static string DisplayLabel =>
        string.IsNullOrWhiteSpace(VersionTitle) ? "FileHub (local build)" : $"FileHub {VersionTitle}";

    /// <summary>Full metadata shown in the tooltip.</summary>
    public static string DetailText
    {
        get
        {
            var lines = new List<string>
            {
                $"{Loc.T("BuildChannel")}: {Loc.T("BuildChannel_" + Channel)}",
                $"{Loc.T("BuildVersion")}: {(string.IsNullOrWhiteSpace(Version) ? "-" : Version)}"
            };
            if (!string.IsNullOrWhiteSpace(Commit)) lines.Add($"Commit: {Commit}");
            if (!string.IsNullOrWhiteSpace(BuildTime)) lines.Add($"Build time: {BuildTime}");
            if (!string.IsNullOrWhiteSpace(RunId)) lines.Add($"Run ID: {RunId}");
            return string.Join('\n', lines);
        }
    }

    private static BuildInfoData Load()
    {
        try
        {
            using var stream = typeof(BuildInfo).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is null) return new BuildInfoData();
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return new BuildInfoData();
            return new BuildInfoData
            {
                Type = Read(root, "type"),
                Version = Read(root, "version"),
                VersionTitle = Read(root, "version_title"),
                Commit = Read(root, "commit"),
                BuildTime = Read(root, "build-time"),
                Action = Read(root, "action")
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            return new BuildInfoData();
        }
    }

    private static string Read(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private sealed class BuildInfoData
    {
        public string Type { get; init; } = "dev";
        public string Version { get; init; } = "0.0.0";
        public string VersionTitle { get; init; } = "local-build";
        public string Commit { get; init; } = string.Empty;
        public string BuildTime { get; init; } = string.Empty;
        public string Action { get; init; } = string.Empty;
    }
}
