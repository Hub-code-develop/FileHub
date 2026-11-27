// Copyright (c) 2026 Hub-code-develop. Licensed under the MIT License.
//
// Folder rules: "when a file matching X lands in folder Y, do Z". This is the automation half of
// a full file browser — download folders that sort themselves, screenshot folders that archive
// after a week, scan folders that move PDFs into a docs tree. Rules run through the VFS, so the
// destination may be a network share.

using System.Text.Json;
using System.Text.RegularExpressions;
using FileHub.Files.Vfs;

namespace FileHub.Files.Services;

public enum WatchAction
{
    MoveTo,
    CopyTo,
    Rename
}

public sealed class WatchRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = string.Empty;
    public string Folder { get; set; } = string.Empty;
    /// <summary>Wildcard pattern such as "*.pdf" or "IMG_*". Empty matches everything.</summary>
    public string Pattern { get; set; } = "*";
    public WatchAction Action { get; set; } = WatchAction.MoveTo;
    public string? Destination { get; set; }
    /// <summary>Rename pattern using the same tokens as batch rename, e.g. "{date}-{name}{ext}".</summary>
    public string? RenamePattern { get; set; }
    public bool Enabled { get; set; } = true;
    public int AppliedCount { get; set; }
    public DateTimeOffset? LastRun { get; set; }
}

public sealed class WatchRuleAppliedEventArgs(WatchRule rule, string source, string target, string? error) : EventArgs
{
    public WatchRule Rule { get; } = rule;
    public string Source { get; } = source;
    public string Target { get; } = target;
    public string? Error { get; } = error;
}

/// <summary>Runs folder rules in the background for the lifetime of the app.</summary>
public sealed class WatchRuleService : IDisposable
{
    public static WatchRuleService Instance { get; } = new();

    private readonly List<(WatchRule Rule, FileSystemWatcher Watcher)> _watchers = [];
    private readonly Lock _gate = new();
    private bool _started;

    public static string RulesPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileHub", "watch-rules.json");

    public event EventHandler<WatchRuleAppliedEventArgs>? RuleApplied;

    public IReadOnlyList<WatchRule> Load()
    {
        try
        {
            if (!File.Exists(RulesPath)) return [];
            return JsonSerializer.Deserialize<List<WatchRule>>(File.ReadAllText(RulesPath)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void Save(IEnumerable<WatchRule> rules)
    {
        try
        {
            var folder = Path.GetDirectoryName(RulesPath)!;
            Directory.CreateDirectory(folder);
            File.WriteAllText(RulesPath, JsonSerializer.Serialize(rules.ToList(),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only profile disables persistence but must not stop the app.
        }
    }

    /// <summary>Starts watching every enabled rule. Called once at startup.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
            foreach (var rule in Load().Where(x => x.Enabled)) Attach(rule);
        }
    }

    public void Reload()
    {
        lock (_gate)
        {
            foreach (var (_, watcher) in _watchers) watcher.Dispose();
            _watchers.Clear();
            foreach (var rule in Load().Where(x => x.Enabled)) Attach(rule);
        }
    }

    private void Attach(WatchRule rule)
    {
        if (!Directory.Exists(rule.Folder)) return;
        try
        {
            var watcher = new FileSystemWatcher(rule.Folder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };
            watcher.Created += (_, e) => _ = ApplyAsync(rule, e.FullPath);
            _watchers.Add((rule, watcher));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // An unwatchable folder just means that rule stays dormant.
        }
    }

    private async Task ApplyAsync(WatchRule rule, string path)
    {
        // Writers create the file before the bytes land; give the producer a moment to finish.
        await Task.Delay(600).ConfigureAwait(false);

        try
        {
            if (!File.Exists(path)) return;
            if (!Matches(rule.Pattern, Path.GetFileName(path))) return;

            var target = rule.Action switch
            {
                WatchAction.Rename => RenamedPath(rule, path),
                WatchAction.CopyTo => null,
                _ => rule.Destination
            };

            if (rule.Action == WatchAction.Rename)
            {
                if (target is not null && !string.Equals(target, path, StringComparison.Ordinal))
                    await VfsFileSystem.Default.RenameAsync(path, Path.GetFileName(target)).ConfigureAwait(false);
                Raise(rule, path, target ?? path, null);
                return;
            }

            if (string.IsNullOrWhiteSpace(rule.Destination)) return;
            var result = rule.Action == WatchAction.CopyTo
                ? await VfsFileSystem.Default.CopyAsync([path], rule.Destination).ConfigureAwait(false)
                : await VfsFileSystem.Default.MoveAsync([path], rule.Destination).ConfigureAwait(false);

            Raise(rule, path, rule.Destination, result.Errors.FirstOrDefault());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Raise(rule, path, rule.Destination ?? string.Empty, ex.Message);
        }
    }

    private static string? RenamedPath(WatchRule rule, string path)
    {
        if (string.IsNullOrWhiteSpace(rule.RenamePattern)) return null;
        var options = new BatchRenameOptions
        {
            Pattern = rule.RenamePattern!,
            IncludeExtensionInPattern = true
        };
        var plan = BatchRenameService.PreviewAsync([path], options).GetAwaiter().GetResult();
        return plan.Items.FirstOrDefault()?.TargetPath;
    }

    private void Raise(WatchRule rule, string source, string target, string? error)
    {
        rule.AppliedCount++;
        rule.LastRun = DateTimeOffset.Now;
        RuleApplied?.Invoke(this, new WatchRuleAppliedEventArgs(rule, source, target, error));
    }

    private static bool Matches(string pattern, string name)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern == "*") return true;
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var (_, watcher) in _watchers) watcher.Dispose();
            _watchers.Clear();
            _started = false;
        }
    }
}
