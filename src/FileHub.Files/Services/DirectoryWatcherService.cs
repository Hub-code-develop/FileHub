using System.IO;

namespace FileHub.Files.Services;

/// <summary>
/// Cross-platform directory change watcher. Raises a single coalesced notification after a burst
/// of FileSystemWatcher events, so a bulk copy does not reload the list once per file.
///
/// Deliberately UI-independent: the desktop layer decides which thread applies the refresh.
/// A watcher that fails (unsupported volume, buffer overflow, permission loss) goes quiet instead
/// of throwing into application code; manual refresh always remains available.
/// </summary>
public sealed class DirectoryWatcherService : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(280);

    private readonly Action _onChanged;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _debounceCts;
    private string? _watchedPath;
    private bool _disposed;

    public DirectoryWatcherService(Action onChanged) => _onChanged = onChanged;

    /// <summary>Path currently observed, or null when watching is stopped.</summary>
    public string? WatchedPath => _watchedPath;

    /// <summary>True when the platform watcher was created successfully.</summary>
    public bool IsWatching => _watcher is { EnableRaisingEvents: true };

    /// <summary>
    /// Watches one directory. Safe to call repeatedly with the same path; a different path
    /// replaces the previous watcher. Passing null or an empty string stops watching.
    /// </summary>
    public void Watch(string? path)
    {
        if (_disposed) return;
        if (string.IsNullOrWhiteSpace(path) || path == _watchedPath && _watcher is not null) return;

        Stop();

        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            _watchedPath = null;
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = false,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                               NotifyFilters.CreationTime | NotifyFilters.LastWrite
            };
            watcher.Created += OnFileEvent;
            watcher.Deleted += OnFileEvent;
            watcher.Renamed += OnFileEvent;
            watcher.Changed += OnFileEvent;
            watcher.Error += OnError;
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
            _watchedPath = path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                      ArgumentException or PlatformNotSupportedException)
        {
            // Network volumes and some virtual filesystems cannot be watched. The app still
            // works: the user can refresh manually with F5.
            _watcher = null;
            _watchedPath = null;
        }
    }

    /// <summary>Stops observing without disposing, so the same instance can watch again.</summary>
    public void Stop()
    {
        _debounceCts?.Cancel();
        _debounceCts = null;

        if (_watcher is not null)
        {
            _watcher.Created -= OnFileEvent;
            _watcher.Deleted -= OnFileEvent;
            _watcher.Renamed -= OnFileEvent;
            _watcher.Changed -= OnFileEvent;
            _watcher.Error -= OnError;
            try { _watcher.EnableRaisingEvents = false; } catch { /* best effort */ }
            _watcher.Dispose();
            _watcher = null;
        }
        _watchedPath = null;
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e) => ScheduleRefresh();

    private void OnError(object sender, ErrorEventArgs e)
    {
        // Buffer overflow: the events were lost, so force a full reload. Re-arming the watcher
        // avoids silently going blind on a busy directory.
        ScheduleRefresh();
        try
        {
            if (_watcher is not null) _watcher.EnableRaisingEvents = true;
        }
        catch { /* watcher stays quiet; manual refresh still works */ }
    }

    private void ScheduleRefresh()
    {
        if (_disposed) return;
        _debounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _debounceCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Debounce, cts.Token);
                if (!cts.IsCancellationRequested && !_disposed) _onChanged();
            }
            catch (OperationCanceledException) { }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
