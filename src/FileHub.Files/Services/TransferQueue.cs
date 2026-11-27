// Copyright (c) 2026 Hub-code-develop. Licensed under the MIT License.
//
// Background transfer queue. Large copies must never block the UI thread, must show real byte
// progress, and must be pausable per item rather than only "cancel everything". Jobs run one at
// a time (sequential I/O is faster than N competing streams) and report through a dispatcher so
// the UI can bind straight to the job objects.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using FileHub.Files.Vfs;

namespace FileHub.Files.Services;

public enum TransferJobState
{
    Queued,
    Running,
    Paused,
    Completed,
    Failed,
    Cancelled
}

public sealed class TransferJob : INotifyPropertyChanged
{
    private readonly Action<Action> _dispatch;

    internal TransferJob(Action<Action> dispatch) => _dispatch = dispatch;

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public ClipboardOperation Operation { get; init; }
    public IReadOnlyList<string> Sources { get; init; } = [];
    public string Destination { get; init; } = string.Empty;
    public VfsConflictPolicy Policy { get; init; } = VfsConflictPolicy.Rename;

    internal CancellationTokenSource Cancellation { get; } = new();
    private TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TransferJobState _state = TransferJobState.Queued;
    private long _bytesDone;
    private long _totalBytes;
    private string _currentItem = string.Empty;
    private string? _error;
    private int _itemsDone;

    public string Title => Sources.Count == 1
        ? VfsPath.Parse(Sources[0]).FileName
        : $"{VfsPath.Parse(Sources[0]).FileName} +{Sources.Count - 1}";

    public string OperationText => Operation == ClipboardOperation.Copy ? "Copy" : "Move";

    public TransferJobState State
    {
        get => _state;
        internal set
        {
            if (_state == value) return;
            _state = value;
            _dispatch(() =>
            {
                Raise(nameof(State));
                Raise(nameof(IsFinished));
                Raise(nameof(IsActive));
                Raise(nameof(StatusText));
            });
        }
    }

    public bool IsFinished => State is TransferJobState.Completed or TransferJobState.Failed
        or TransferJobState.Cancelled;

    public bool IsActive => State is TransferJobState.Running or TransferJobState.Paused;

    public long BytesDone { get => _bytesDone; private set { _bytesDone = value; Raise(nameof(BytesDone)); Raise(nameof(ProgressText)); } }
    public long TotalBytes { get => _totalBytes; private set { _totalBytes = value; Raise(nameof(TotalBytes)); Raise(nameof(ProgressText)); } }

    public double Fraction => TotalBytes <= 0 ? 0 : Math.Clamp((double)BytesDone / TotalBytes, 0, 1);

    public string CurrentItem { get => _currentItem; private set { _currentItem = value; Raise(nameof(CurrentItem)); } }

    public string? Error { get => _error; private set { _error = value; Raise(nameof(Error)); Raise(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(_error);

    public int ItemsDone { get => _itemsDone; private set { _itemsDone = value; Raise(nameof(ItemsDone)); } }

    public string ProgressText => TotalBytes <= 0
        ? FileSystemService.FormatSize(BytesDone)
        : $"{FileSystemService.FormatSize(BytesDone)} / {FileSystemService.FormatSize(TotalBytes)}";

    public string StatusText => State switch
    {
        TransferJobState.Queued => "Queued",
        TransferJobState.Running => ProgressText,
        TransferJobState.Paused => "Paused",
        TransferJobState.Completed => "Completed",
        TransferJobState.Failed => "Failed",
        _ => "Cancelled"
    };

    internal void Update(VfsTransferProgress progress)
    {
        _dispatch(() =>
        {
            BytesDone = progress.BytesDone;
            TotalBytes = progress.TotalBytes;
            CurrentItem = progress.ItemName;
            ItemsDone = progress.ItemIndex;
            Raise(nameof(Fraction));
        });
    }

    internal void Fail(string message) => _dispatch(() => Error = message);

    /// <summary>Blocks while the job is paused; also honours cancellation.</summary>
    internal async Task CheckpointAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (State != TransferJobState.Paused) return;
        await _resume.Task.WaitAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    public void Pause()
    {
        if (State != TransferJobState.Running) return;
        _resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        State = TransferJobState.Paused;
    }

    public void Resume()
    {
        if (State != TransferJobState.Paused) return;
        State = TransferJobState.Running;
        _resume.TrySetResult();
    }

    public void Cancel() => Cancellation.Cancel();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Sequential transfer queue with pause, cancel and a conflict callback owned by the UI.</summary>
public sealed class TransferQueue : IDisposable
{
    private readonly Queue<TransferJob> _pending = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Lock _gate = new();
    private readonly Action<Action> _dispatch;
    private Task? _worker;
    private bool _disposed;

    /// <param name="dispatch">
    /// Marshals property updates onto the UI thread (Avalonia's Dispatcher.UIThread.Post).
    /// When null, updates run inline, which is what tests want.
    /// </param>
    public TransferQueue(Action<Action>? dispatch = null)
    {
        _dispatch = dispatch ?? (action => action());
    }

    public ObservableCollection<TransferJob> Jobs { get; } = [];

    /// <summary>
    /// Asked whenever a target already exists. Returning Cancel stops the whole job.
    /// The UI decides whether an "apply to all" checkbox is in effect.
    /// </summary>
    public Func<string, Task<VfsConflictAnswer>>? ConflictResolver { get; set; }

    /// <summary>Raised on the dispatcher after a job reaches a terminal state.</summary>
    public event EventHandler<TransferJob>? JobFinished;

    public TransferJob Enqueue(IReadOnlyList<string> sources, string destination,
        ClipboardOperation operation, VfsConflictPolicy policy = VfsConflictPolicy.Rename)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0) throw new ArgumentException("No sources", nameof(sources));

        var job = new TransferJob(_dispatch)
        {
            Operation = operation,
            Sources = sources.ToArray(),
            Destination = destination,
            Policy = policy
        };
        _dispatch(() => Jobs.Add(job));
        lock (_gate) _pending.Enqueue(job);
        EnsureWorker();
        _signal.Release();
        return job;
    }

    public void Cancel(TransferJob job)
    {
        job.Cancel();
        if (job.State == TransferJobState.Queued) job.State = TransferJobState.Cancelled;
    }

    public void CancelAll()
    {
        foreach (var job in Jobs) Cancel(job);
    }

    public void Pause(TransferJob job) => job.Pause();
    public void Resume(TransferJob job) => job.Resume();

    public void ClearFinished()
    {
        _dispatch(() =>
        {
            for (var i = Jobs.Count - 1; i >= 0; i--)
                if (Jobs[i].IsFinished) Jobs.RemoveAt(i);
        });
    }

    private void EnsureWorker()
    {
        lock (_gate)
        {
            _worker ??= Task.Run(WorkerLoopAsync);
        }
    }

    private async Task WorkerLoopAsync()
    {
        while (!_disposed)
        {
            TransferJob? job;
            lock (_gate)
            {
                job = _pending.Count > 0 ? _pending.Dequeue() : null;
            }

            if (job is null)
            {
                await _signal.WaitAsync().ConfigureAwait(false);
                continue;
            }

            if (job.State == TransferJobState.Cancelled) continue;
            await RunAsync(job).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(TransferJob job)
    {
        job.State = TransferJobState.Running;
        var progress = new Progress<VfsTransferProgress>(job.Update);
        var ct = job.Cancellation.Token;
        try
        {
            var result = job.Operation == ClipboardOperation.Copy
                ? await VfsFileSystem.Default.CopyAsync(job.Sources, job.Destination, job.Policy,
                    ConflictResolver, progress, ct, job.CheckpointAsync).ConfigureAwait(false)
                : await VfsFileSystem.Default.MoveAsync(job.Sources, job.Destination, job.Policy,
                    ConflictResolver, progress, ct, job.CheckpointAsync).ConfigureAwait(false);

            if (result.Errors.Count > 0) job.Fail(string.Join("\n", result.Errors.Take(4)));
            job.State = result.Cancelled
                ? TransferJobState.Cancelled
                : result.Errors.Count > 0 && result.Succeeded == 0
                    ? TransferJobState.Failed
                    : TransferJobState.Completed;
        }
        catch (OperationCanceledException)
        {
            job.State = TransferJobState.Cancelled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException or ArgumentException)
        {
            job.Fail(ex.Message);
            job.State = TransferJobState.Failed;
        }
        finally
        {
            JobFinished?.Invoke(this, job);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        CancelAll();
        _signal.Release();
        _signal.Dispose();
    }
}
