# Writing a FileHub plugin

A plugin is a folder under the user profile:

```
%APPDATA%/FileHub/plugins/           (Linux: ~/.config/FileHub/plugins)
└── my-plugin/
    ├── plugin.json
    └── MyPlugin.dll
```

`%APPDATA%` is `Environment.SpecialFolder.ApplicationData`; on macOS that is
`~/Library/Application Support`, on Linux `~/.config`.

## plugin.json

```json
{
  "id": "com.example.csv-tools",
  "name": "CSV tools",
  "version": "1.0.0",
  "author": "Example",
  "description": "Adds a row counter and a preview handler for CSV files.",
  "entryAssembly": "CsvTools.dll",
  "entryType": "CsvTools.Plugin",
  "minHostVersion": "1.0.0",
  "license": "MIT"
}
```

`entryType` is optional; without it the loader takes the first public type implementing
`IFileHubPlugin`.

## Implementation

Reference `FileHub.Files.dll` (the host loads its own copy, so do **not** ship a second one).

```csharp
using FileHub.Files.Models;
using FileHub.Files.Plugin;

namespace CsvTools;

public sealed class Plugin : IFileHubPlugin
{
    public string Id => "com.example.csv-tools";
    public string Name => "CSV tools";
    public string Version => "1.0.0";

    public void Initialize(IPluginContext context)
    {
        context.RegisterCommand(new PluginCommand
        {
            Id = "csv.count-rows",
            Title = "Count CSV rows",
            Category = "CSV",
            Keywords = "csv rows lines count",
            Execute = async ctx =>
            {
                var file = ctx.SelectedPaths.FirstOrDefault();
                if (file is null) { ctx.Report("CSV", "Select a CSV file first."); return; }
                using var stream = await FileHub.Files.Vfs.VfsFileSystem.Default
                    .OpenReadAsync(file, CancellationToken.None);
                using var reader = new StreamReader(stream);
                var rows = 0;
                while (await reader.ReadLineAsync() is not null) rows++;
                ctx.Report("CSV", $"{rows} rows");
            }
        });

        context.RegisterColumn(new PluginColumn
        {
            Id = "csv.rows",
            Header = "Rows",
            GetValue = entry => entry.Extension.Equals(".csv", StringComparison.OrdinalIgnoreCase)
                ? "?"
                : null
        });

        context.RegisterPreviewHandler(new PluginPreviewHandler
        {
            Id = "csv.preview",
            CanHandle = path => path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase),
            RenderAsync = async (path, open, ct) =>
            {
                await using var stream = await open(ct);
                using var reader = new StreamReader(stream);
                var head = new List<string>();
                for (var i = 0; i < 5 && await reader.ReadLineAsync(ct) is { } line; i++)
                    head.Add(line);
                return new PluginPreview { Text = string.Join('\n', head) };
            }
        });
    }
}
```

## What the host guarantees

- Each plugin loads into its own collectible `AssemblyLoadContext` with an
  `AssemblyDependencyResolver` rooted at the plugin folder. Two plugins may ship different
  versions of the same dependency.
- `FileHub.Files.dll` is always resolved from the host, so plugin and host share one type identity
  for `IFileHubPlugin`, `FileEntry` and `IFileSystemProvider`.
- An exception thrown from `Initialize` marks the plugin `Failed` with the message; startup
  continues. An exception thrown from a command or preview handler is the command's problem, not
  the host's — catch it and report.

## What the host does not do

- No sandbox. A plugin runs in-process with the app's own file-system access.
- No UI injection beyond the registered commands, columns and preview handlers.
- No version negotiation beyond `minHostVersion`, which is compared as a string prefix today.
- No automatic update or signature check; installing a plugin is the same act of trust as running
  any other .NET assembly.

## Disabling a plugin

Write the plugin id into `plugins/disabled.json`:

```json
["com.example.csv-tools"]
```

The plugin still loads its manifest (so it can be listed) but its assembly is not loaded.
