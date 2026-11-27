// Copyright (c) 2026 Hub-code-develop. Licensed under the MIT License.
//
// Plugin host. Each plugin loads into its own collectible AssemblyLoadContext with a resolver
// rooted at the plugin folder, so two plugins may ship different versions of the same library.
// A plugin that throws during initialization is isolated: it is marked Failed and the host keeps
// running, because a broken add-on must never take the file manager down.

using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using FileHub.Files.Models;
using FileHub.Files.Plugin;
using FileHub.Files.Vfs;

namespace FileHub.Desktop.Services;

public sealed class PluginHost
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static PluginHost Instance { get; } = new();

    private readonly List<LoadedPlugin> _plugins = [];
    private readonly List<AssemblyLoadContext> _contexts = [];
    private bool _loaded;

    public IReadOnlyList<LoadedPlugin> Plugins => _plugins;
    public IReadOnlyList<PluginCommand> Commands => _plugins.SelectMany(x => x.Commands).ToList();
    public IReadOnlyList<PluginColumn> Columns => _plugins.SelectMany(x => x.Columns).ToList();
    public IReadOnlyList<PluginPreviewHandler> PreviewHandlers => _plugins.SelectMany(x => x.PreviewHandlers).ToList();

    public static string PluginDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileHub", "plugins");

    /// <summary>Scans the plugin directory once. Safe to call repeatedly.</summary>
    public void Load()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            if (!Directory.Exists(PluginDirectory)) return;
            var disabled = ReadDisabled();

            foreach (var folder in Directory.EnumerateDirectories(PluginDirectory))
            {
                var manifestPath = Path.Combine(folder, "plugin.json");
                if (!File.Exists(manifestPath)) continue;

                LoadedPlugin loaded;
                try
                {
                    var manifest = JsonSerializer.Deserialize<PluginManifest>(
                        File.ReadAllText(manifestPath), JsonOptions);
                    if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
                    {
                        continue;
                    }
                    loaded = new LoadedPlugin { Manifest = manifest, Folder = folder };
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (disabled.Contains(loaded.Manifest.Id))
                {
                    loaded.State = PluginLoadState.Disabled;
                    _plugins.Add(loaded);
                    continue;
                }

                _plugins.Add(loaded);
                LoadAssembly(loaded);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only or missing profile just means no plugins.
        }
    }

    private void LoadAssembly(LoadedPlugin plugin)
    {
        var assemblyFile = string.IsNullOrWhiteSpace(plugin.Manifest.EntryAssembly)
            ? Directory.EnumerateFiles(plugin.Folder, "*.dll").FirstOrDefault()
            : Path.Combine(plugin.Folder, plugin.Manifest.EntryAssembly);

        if (assemblyFile is null || !File.Exists(assemblyFile))
        {
            plugin.State = PluginLoadState.Failed;
            plugin.Error = "Entry assembly not found.";
            return;
        }

        try
        {
            var context = new PluginLoadContext(assemblyFile);
            _contexts.Add(context);
            var assembly = context.LoadFromAssemblyPath(assemblyFile);

            var type = string.IsNullOrWhiteSpace(plugin.Manifest.EntryType)
                ? assembly.GetTypes().FirstOrDefault(t => typeof(IFileHubPlugin).IsAssignableFrom(t) && !t.IsAbstract)
                : assembly.GetType(plugin.Manifest.EntryType!);

            if (type is null || Activator.CreateInstance(type) is not IFileHubPlugin instance)
            {
                plugin.State = PluginLoadState.Failed;
                plugin.Error = "No IFileHubPlugin implementation found.";
                return;
            }

            var registrar = new PluginContext(plugin);
            instance.Initialize(registrar);
            plugin.Instance = instance;
            plugin.State = PluginLoadState.Loaded;
        }
        catch (Exception ex)
        {
            plugin.State = PluginLoadState.Failed;
            plugin.Error = ex.Message;
        }
    }

    private static HashSet<string> ReadDisabled()
    {
        try
        {
            var file = Path.Combine(PluginDirectory, "disabled.json");
            if (!File.Exists(file)) return [];
            var ids = JsonSerializer.Deserialize<string[]>(File.ReadAllText(file)) ?? [];
            return new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private sealed class PluginContext(LoadedPlugin plugin) : IPluginContext
    {
        public string HostVersion => BuildInfo.Version;

        public void RegisterCommand(PluginCommand command)
        {
            if (!string.IsNullOrWhiteSpace(command.Id)) plugin.Commands.Add(command);
        }

        public void RegisterColumn(PluginColumn column)
        {
            if (!string.IsNullOrWhiteSpace(column.Id)) plugin.Columns.Add(column);
        }

        public void RegisterPreviewHandler(PluginPreviewHandler handler)
        {
            if (!string.IsNullOrWhiteSpace(handler.Id)) plugin.PreviewHandlers.Add(handler);
        }

        public void RegisterProvider(IFileSystemProvider provider) => VfsRegistry.Register(provider);

        public void Log(string message) => System.Diagnostics.Debug.WriteLine($"[plugin:{plugin.Manifest.Id}] {message}");
    }

    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public PluginLoadContext(string mainAssemblyPath) : base(isCollectible: true)
            => _resolver = new AssemblyDependencyResolver(mainAssemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // The contracts live in the host: always share that instance so type identity matches.
            if (assemblyName.Name == typeof(IFileHubPlugin).Assembly.GetName().Name) return null;
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }
}
