// Copyright (c) 2026 Hub-code-develop. Licensed under the MIT License.
//
// Plugin contracts. A plugin is a .NET assembly plus a plugin.json manifest; it gets a context
// object at initialization and may contribute commands, list columns, preview handlers and even
// a whole file-system provider. Everything here is intentionally small: the host owns the UI,
// the plugin owns behaviour.

using FileHub.Files.Models;
using FileHub.Files.Vfs;

namespace FileHub.Files.Plugin;

public sealed class PluginManifest
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string? Author { get; set; }
    public string? Description { get; set; }
    /// <summary>Assembly file name relative to the plugin folder, e.g. "MyPlugin.dll".</summary>
    public string EntryAssembly { get; set; } = string.Empty;
    public string? EntryType { get; set; }
    public string? Homepage { get; set; }
    public string? License { get; set; }
    /// <summary>Minimum host version; the loader refuses older hosts.</summary>
    public string? MinHostVersion { get; set; }
}

public sealed class PluginCommandContext
{
    public required string CurrentPath { get; init; }
    public required IReadOnlyList<string> SelectedPaths { get; init; }
    public required Func<string, Task> NavigateAsync { get; init; }
    public required Action<string, string> Report { get; init; }
}

public sealed class PluginCommand
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string Category { get; init; } = "Plugin";
    public string Keywords { get; init; } = string.Empty;
    public required Func<PluginCommandContext, Task> Execute { get; init; }
}

public sealed class PluginColumn
{
    public required string Id { get; init; }
    public required string Header { get; init; }
    public double Width { get; init; } = 100;
    public required Func<FileEntry, string?> GetValue { get; init; }
}

public sealed class PluginPreview
{
    public string? Text { get; init; }
    public IReadOnlyList<(string Label, string Value)> Fields { get; init; } = [];
    public string? Notice { get; init; }
}

public sealed class PluginPreviewHandler
{
    public required string Id { get; init; }
    public required Func<string, bool> CanHandle { get; init; }
    public required Func<string, Func<CancellationToken, Task<Stream>>, CancellationToken, Task<PluginPreview>> RenderAsync { get; init; }
}

public interface IPluginContext
{
    string HostVersion { get; }
    void RegisterCommand(PluginCommand command);
    void RegisterColumn(PluginColumn column);
    void RegisterPreviewHandler(PluginPreviewHandler handler);
    void RegisterProvider(IFileSystemProvider provider);
    void Log(string message);
}

public interface IFileHubPlugin
{
    string Id { get; }
    string Name { get; }
    string Version { get; }
    void Initialize(IPluginContext context);
}

public enum PluginLoadState
{
    Loaded,
    Failed,
    Disabled,
    Incompatible
}

public sealed class LoadedPlugin
{
    public required PluginManifest Manifest { get; init; }
    public required string Folder { get; init; }
    public PluginLoadState State { get; set; }
    public string? Error { get; set; }
    public IFileHubPlugin? Instance { get; set; }
    public List<PluginCommand> Commands { get; } = [];
    public List<PluginColumn> Columns { get; } = [];
    public List<PluginPreviewHandler> PreviewHandlers { get; } = [];

    public string DisplayName => string.IsNullOrWhiteSpace(Manifest.Name) ? Manifest.Id : Manifest.Name;
}
