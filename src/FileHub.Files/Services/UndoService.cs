// Copyright (c) 2026 Hub-code-develop. Licensed under the MIT License.
//
// Undo/redo stack plus the three operations the file manager exposes: rename, create folder and
// transfer. Nothing here touches the file system directly: every operation receives delegates
// from the caller, which is what lets an undo of "extract from zip to a NAS share" work without
// the engine knowing what a zip or a share is.
//
// Undo must always be reliable. Redo is best effort: it re-runs the recorded work through the
// same delegates, so it fails loudly (and the operation returns to the undo stack) when the
// source no longer exists.

namespace FileHub.Files.Services;

/// <summary>One reversible user action.</summary>
public interface IUndoableOperation
{
    /// <summary>Short, user-facing description such as "Rename 3 items".</summary>
    string Title { get; }

    /// <summary>Reverts the action.</summary>
    Task UndoAsync(CancellationToken ct);

    /// <summary>Re-applies the action after it was undone.</summary>
    Task RedoAsync(CancellationToken ct);
}

/// <summary>
/// A bounded undo/redo stack. State changes are guarded by a lock; the operations themselves run
/// outside the lock so a long transfer never blocks the UI thread's readers.
/// </summary>
public sealed class UndoService
{
    private readonly List<IUndoableOperation> _undo = [];
    private readonly List<IUndoableOperation> _redo = [];
    private readonly Lock _gate = new();

    /// <summary>Maximum number of remembered operations; the oldest is dropped first. Minimum 1.</summary>
    public int Capacity { get; init; } = 100;

    /// <summary>Raised after any change to the stacks, undo/redo included.</summary>
    public event EventHandler? Changed;

    /// <summary>True when there is an operation to undo.</summary>
    public bool CanUndo
    {
        get
        {
            lock (_gate)
            {
                return _undo.Count > 0;
            }
        }
    }

    /// <summary>True when there is an undone operation to redo.</summary>
    public bool CanRedo
    {
        get
        {
            lock (_gate)
            {
                return _redo.Count > 0;
            }
        }
    }

    /// <summary>Title of the operation <see cref="UndoAsync"/> would revert, or null.</summary>
    public string? UndoTitle
    {
        get
        {
            lock (_gate)
            {
                return _undo.Count > 0 ? _undo[^1].Title : null;
            }
        }
    }

    /// <summary>Title of the operation <see cref="RedoAsync"/> would re-apply, or null.</summary>
    public string? RedoTitle
    {
        get
        {
            lock (_gate)
            {
                return _redo.Count > 0 ? _redo[^1].Title : null;
            }
        }
    }

    /// <summary>Records a completed operation. The redo stack is cleared, as in every editor.</summary>
    public void Push(IUndoableOperation op)
    {
        ArgumentNullException.ThrowIfNull(op);
        lock (_gate)
        {
            _undo.Add(op);
            _redo.Clear();
            Trim(_undo);
        }

        RaiseChanged();
    }

    /// <summary>
    /// Reverts the most recent operation. On failure the operation goes back on the undo stack and
    /// the exception is rethrown so the caller can surface it; the stack is never left corrupted.
    /// </summary>
    public async Task<IUndoableOperation?> UndoAsync(CancellationToken ct = default)
    {
        IUndoableOperation op;
        lock (_gate)
        {
            if (_undo.Count == 0)
            {
                return null;
            }

            op = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
        }

        try
        {
            await op.UndoAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                _undo.Add(op);
                Trim(_undo);
            }

            RaiseChanged();
            throw;
        }

        lock (_gate)
        {
            _redo.Add(op);
            Trim(_redo);
        }

        RaiseChanged();
        return op;
    }

    /// <summary>
    /// Re-applies the most recently undone operation. On failure the operation returns to the redo
    /// stack and the exception is rethrown.
    /// </summary>
    public async Task<IUndoableOperation?> RedoAsync(CancellationToken ct = default)
    {
        IUndoableOperation op;
        lock (_gate)
        {
            if (_redo.Count == 0)
            {
                return null;
            }

            op = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
        }

        try
        {
            await op.RedoAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                _redo.Add(op);
                Trim(_redo);
            }

            RaiseChanged();
            throw;
        }

        lock (_gate)
        {
            _undo.Add(op);
            Trim(_undo);
        }

        RaiseChanged();
        return op;
    }

    /// <summary>Drops both stacks, for example after navigating to another location.</summary>
    public void Clear()
    {
        var changed = false;
        lock (_gate)
        {
            if (_undo.Count > 0 || _redo.Count > 0)
            {
                changed = true;
            }

            _undo.Clear();
            _redo.Clear();
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    private void Trim(List<IUndoableOperation> stack)
    {
        var capacity = Math.Max(1, Capacity);
        while (stack.Count > capacity)
        {
            stack.RemoveAt(0);
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>One recorded rename. Both paths are full paths: where the entry was and where it is.</summary>
public sealed record RenameRecord(string From, string To);

/// <summary>
/// Rename of one entry or a whole batch. Both paths of a record are full paths; the delegate
/// follows the same contract as the rest of the project - "rename this path to this file name" -
/// so the caller never has to translate paths. The undo replays the moves in reverse order, which
/// is what makes a shift such as a -> b, b -> c unwind safely.
/// </summary>
public sealed class RenameOperation : IUndoableOperation
{
    private readonly IReadOnlyList<RenameRecord> _moves;
    private readonly Func<string, string, CancellationToken, Task> _renameAsync;

    /// <summary>Creates a batch rename operation.</summary>
    /// <param name="title">Title shown in the undo menu.</param>
    /// <param name="moves">Completed renames (full path before, full path after), in order.</param>
    /// <param name="renameAsync">Delegate that renames "source path" to "new file name".</param>
    public RenameOperation(
        string title,
        IReadOnlyList<RenameRecord> moves,
        Func<string, string, CancellationToken, Task> renameAsync)
    {
        ArgumentNullException.ThrowIfNull(moves);
        ArgumentNullException.ThrowIfNull(renameAsync);
        Title = title;
        _moves = moves;
        _renameAsync = renameAsync;
    }

    /// <summary>Creates a single rename operation. Both arguments are full paths.</summary>
    public RenameOperation(
        string from, string to, Func<string, string, CancellationToken, Task> renameAsync)
        : this($"Rename {VfsName(from)}", [new RenameRecord(from, to)], renameAsync)
    {
    }

    /// <inheritdoc />
    public string Title { get; }

    /// <inheritdoc />
    public Task UndoAsync(CancellationToken ct) => ReplayAsync(reverse: true, ct);

    /// <inheritdoc />
    public Task RedoAsync(CancellationToken ct) => ReplayAsync(reverse: false, ct);

    private async Task ReplayAsync(bool reverse, CancellationToken ct)
    {
        // Undo walks backwards because a batch may depend on its own ordering.
        var sequence = reverse ? _moves.Reverse() : _moves;
        foreach (var move in sequence)
        {
            ct.ThrowIfCancellationRequested();

            // The entry currently lives at "to" when undoing and at "from" when redoing.
            var sourcePath = reverse ? move.To : move.From;
            var targetPath = reverse ? move.From : move.To;
            var targetName = VfsName(targetPath);

            try
            {
                await _renameAsync(sourcePath, targetName, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var action = reverse ? "Undo" : "Redo";
                throw new IOException($"{action} of '{Title}' failed at '{targetPath}': {ex.Message}", ex);
            }
        }
    }

    private static string VfsName(string path)
    {
        try
        {
            return Vfs.VfsPath.Parse(path).FileName;
        }
        catch (ArgumentException)
        {
            return path;
        }
    }
}

/// <summary>
/// Creation of a single folder. Undo removes it again and therefore requires it to be empty; the
/// injected delete delegate is expected to refuse non-empty directories rather than recurse.
/// </summary>
public sealed class CreateFolderOperation : IUndoableOperation
{
    private readonly Func<string, CancellationToken, Task> _deleteDirectoryAsync;
    private readonly Func<string, CancellationToken, Task> _createDirectoryAsync;

    /// <summary>Creates the operation for an already created folder.</summary>
    /// <param name="path">Folder that was created.</param>
    /// <param name="deleteDirectoryAsync">Delegate that deletes a folder (non-recursive).</param>
    /// <param name="createDirectoryAsync">Delegate that recreates the folder on redo.</param>
    /// <param name="title">Title shown in the undo menu.</param>
    public CreateFolderOperation(
        string path,
        Func<string, CancellationToken, Task> deleteDirectoryAsync,
        Func<string, CancellationToken, Task> createDirectoryAsync,
        string title = "Create folder")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(deleteDirectoryAsync);
        ArgumentNullException.ThrowIfNull(createDirectoryAsync);
        Path = path;
        Title = title;
        _deleteDirectoryAsync = deleteDirectoryAsync;
        _createDirectoryAsync = createDirectoryAsync;
    }

    /// <summary>Folder that was created.</summary>
    public string Path { get; }

    /// <inheritdoc />
    public string Title { get; }

    /// <inheritdoc />
    public async Task UndoAsync(CancellationToken ct)
    {
        try
        {
            await _deleteDirectoryAsync(Path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new IOException($"Undo of '{Title}' failed at '{Path}': {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public async Task RedoAsync(CancellationToken ct)
    {
        try
        {
            await _createDirectoryAsync(Path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new IOException($"Redo of '{Title}' failed at '{Path}': {ex.Message}", ex);
        }
    }
}

/// <summary>Whether a recorded transfer was a copy or a move.</summary>
public enum TransferKind
{
    Copy,
    Move
}

/// <summary>One transferred entry: where it came from and where it landed.</summary>
public sealed record TransferRecord(string SourcePath, string TargetPath);

/// <summary>
/// A copy or move of one or more entries. Undo deletes the copies and moves the moved entries
/// back; both walk the records in reverse so nested folders unwind from the inside out.
/// </summary>
public sealed class TransferOperation : IUndoableOperation
{
    private readonly TransferKind _kind;
    private readonly IReadOnlyList<TransferRecord> _items;
    private readonly Func<string, string, CancellationToken, Task> _copyAsync;
    private readonly Func<string, string, CancellationToken, Task> _moveAsync;
    private readonly Func<string, CancellationToken, Task> _deleteAsync;

    /// <summary>Creates a transfer operation from completed transfers.</summary>
    /// <param name="title">Title shown in the undo menu.</param>
    /// <param name="kind">Whether the entries were copied or moved.</param>
    /// <param name="items">Completed transfers, in the order they finished.</param>
    /// <param name="copyAsync">Delegate that copies "source path" to "target path".</param>
    /// <param name="moveAsync">Delegate that moves "source path" to "target path".</param>
    /// <param name="deleteAsync">Delegate that deletes a file or folder recursively.</param>
    public TransferOperation(
        string title,
        TransferKind kind,
        IReadOnlyList<TransferRecord> items,
        Func<string, string, CancellationToken, Task> copyAsync,
        Func<string, string, CancellationToken, Task> moveAsync,
        Func<string, CancellationToken, Task> deleteAsync)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(copyAsync);
        ArgumentNullException.ThrowIfNull(moveAsync);
        ArgumentNullException.ThrowIfNull(deleteAsync);
        Title = title;
        _kind = kind;
        _items = items;
        _copyAsync = copyAsync;
        _moveAsync = moveAsync;
        _deleteAsync = deleteAsync;
    }

    /// <inheritdoc />
    public string Title { get; }

    /// <summary>Whether the recorded work was a copy or a move.</summary>
    public TransferKind Kind => _kind;

    /// <inheritdoc />
    public async Task UndoAsync(CancellationToken ct)
    {
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            ct.ThrowIfCancellationRequested();
            var item = _items[i];

            try
            {
                if (_kind == TransferKind.Copy)
                {
                    // The copy is the only new artifact; the original never moved.
                    await _deleteAsync(item.TargetPath, ct).ConfigureAwait(false);
                }
                else
                {
                    await _moveAsync(item.TargetPath, item.SourcePath, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new IOException(
                    $"Undo of '{Title}' failed at '{item.TargetPath}': {ex.Message}", ex);
            }
        }
    }

    /// <inheritdoc />
    public async Task RedoAsync(CancellationToken ct)
    {
        // Redo replays the recorded transfer through the same delegates. It therefore needs the
        // source to still exist; when it does not, the delegate throws and the operation returns
        // to the redo stack, which is the honest outcome rather than a silent half-redo.
        foreach (var item in _items)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (_kind == TransferKind.Copy)
                {
                    await _copyAsync(item.SourcePath, item.TargetPath, ct).ConfigureAwait(false);
                }
                else
                {
                    await _moveAsync(item.SourcePath, item.TargetPath, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new IOException(
                    $"Redo of '{Title}' failed at '{item.SourcePath}': {ex.Message}", ex);
            }
        }
    }
}
