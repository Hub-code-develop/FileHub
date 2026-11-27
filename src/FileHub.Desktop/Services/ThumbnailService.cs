// Copyright (c) 2026 Hub-code-develop. Licensed under the MIT License.
//
// Avalonia-side thumbnail decoding and a bounded, path-keyed cache. The format probe lives in
// FileHub.Files/PreviewService.cs so the logic project stays free of UI dependencies.

using Avalonia.Media.Imaging;
using FileHub.Files.Services;
using FileHub.Files.Vfs;

namespace FileHub.Desktop.Services;

/// <summary>
/// Decodes image files into Avalonia bitmaps off the UI thread and caches the result.
/// Three widths mirror the three places Files App shows a thumbnail: inline list rows,
/// icon-grid tiles and the preview area of the details pane.
/// </summary>
public sealed class ThumbnailService
{
    public const int ListWidth = 32;
    public const int GridWidth = 128;
    public const int PaneWidth = 512;

    private const int MaxCached = 240;

    private readonly Dictionary<string, Bitmap> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _lru = new();
    private readonly object _gate = new();

    public static ThumbnailService Instance { get; } = new();

    private ThumbnailService()
    {
    }

    public Bitmap? TryGet(string path, int width)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        lock (_gate)
        {
            return _cache.TryGetValue(Key(path, width), out var bitmap) ? bitmap : null;
        }
    }

    /// <summary>
    /// Returns a decoded thumbnail, or null when the file is not a supported image or cannot be
    /// read. Decoding happens on a worker thread; callers assign the result on the UI thread.
    /// </summary>
    public async Task<Bitmap?> LoadAsync(string path, int width, CancellationToken cancellationToken = default)
    {
        if (!PreviewService.CanShowImage(path)) return null;

        var cached = TryGet(path, width);
        if (cached is not null) return cached;

        try
        {
            var virtualPath = VfsPath.IsVirtualPath(path);
            var bitmap = await Task.Run(async () =>
            {
                if (virtualPath)
                {
                    // Archive and network entries have no local file handle; stream them through
                    // the provider so a thumbnail inside a zip still decodes.
                    await using var remote = await VfsFileSystem.Default.OpenReadAsync(path, cancellationToken)
                        .ConfigureAwait(false);
                    using var buffer = await CopyToSeekableAsync(remote, cancellationToken).ConfigureAwait(false);
                    return Bitmap.DecodeToWidth(buffer, width, BitmapInterpolationMode.HighQuality);
                }

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan);
                return Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.HighQuality);
            }, cancellationToken);

            if (bitmap is null) return null;

            lock (_gate)
            {
                var key = Key(path, width);
                if (_cache.TryGetValue(key, out var previous))
                {
                    _cache.Remove(key);
                    // A bitmap may still be bound to a virtualised row. Let the GC collect it
                    // rather than disposing something the renderer might still reference.
                    _ = previous;
                }
                _cache[key] = bitmap;
                _lru.Enqueue(key);
                while (_lru.Count > MaxCached)
                {
                    var oldest = _lru.Dequeue();
                    _cache.Remove(oldest);
                }
            }
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException or ArgumentException
                                       or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes an already-extracted byte buffer (for example the cover art inside an MP3) without
    /// touching the file system. Not cached: the caller owns the lifetime.
    /// </summary>
    public Task<Bitmap?> LoadFromBytesAsync(byte[] bytes, int width, CancellationToken cancellationToken = default)
    {
        if (bytes.Length == 0) return Task.FromResult<Bitmap?>(null);
        return Task.Run<Bitmap?>(() =>
        {
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                return Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.HighQuality);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException)
            {
                return null;
            }
        }, cancellationToken);
    }

    /// <summary>Drops cached variants after a rename, delete or overwrite.</summary>
    public void Invalidate(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (_gate)
        {
            foreach (var width in new[] { ListWidth, GridWidth, PaneWidth })
                _cache.Remove(Key(path, width));
        }
    }

    /// <summary>Decoders need a seekable stream; providers may hand back a forward-only one.</summary>
    private static async Task<MemoryStream> CopyToSeekableAsync(Stream source, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, ct).ConfigureAwait(false);
        buffer.Position = 0;
        return buffer;
    }

    private static string Key(string path, int width) => width + "|" + path;
}
