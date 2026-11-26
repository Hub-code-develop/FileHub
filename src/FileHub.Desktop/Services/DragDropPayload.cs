using System.Text.Json;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace FileHub.Desktop.Services;

/// <summary>
/// Payload contract for file drag-and-drop.
///
/// Internally FileHub passes a newline-joined path list under a private application format, which
/// keeps the operation a MOVE. When the drag originates outside the app (Finder / Explorer),
/// Avalonia exposes IStorageItem(s) through DataFormat.File - those are always imported as a COPY
/// because the caller does not own the source and should not delete it.
/// </summary>
public static class DragDropPayload
{
    private static readonly DataFormat<string> PathsFormat =
        DataFormat.CreateStringApplicationFormat("filehub.paths.v1");

    /// <summary>Builds the data transfer carried by a drag started inside the file list.</summary>
    public static IDataTransfer Create(IEnumerable<string> paths, bool copy)
    {
        var transfer = new DataTransfer();
        var list = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        var item = new DataTransferItem();
        item.Set(PathsFormat, JsonSerializer.Serialize(new Payload { Copy = copy, Paths = list }));
        // Also publish plain text so a drop on Explorer/Finder has a readable fallback.
        item.SetText(string.Join(Environment.NewLine, list));
        transfer.Add(item);
        return transfer;
    }

    /// <summary>
    /// Extracts drop data. Internal drags win over external IStorageItem paths so a move inside
    /// FileHub is never downgraded to a copy. Returns false when the drag has no file payload.
    /// </summary>
    public static bool TryRead(IDataTransfer? data, out string[] paths, out bool copy)
    {
        paths = [];
        copy = true;
        if (data is null) return false;

        if (data.TryGetValue(PathsFormat) is { } json)
        {
            try
            {
                var payload = JsonSerializer.Deserialize<Payload>(json);
                if (payload?.Paths is { Length: > 0 })
                {
                    paths = payload.Paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
                    copy = payload.Copy;
                    return paths.Length > 0;
                }
            }
            catch (JsonException) { /* fall through to external formats */ }
        }

        var files = data.TryGetFiles();
        if (files is { Length: > 0 })
        {
            paths = files.Select(x => x.TryGetLocalPath()).OfType<string>().ToArray();
            copy = true;
            return paths.Length > 0;
        }

        // Text fallback: some platforms only deliver a URI/text list. Accept only absolute local
        // paths that still exist, so arbitrary text dragged over the window cannot trigger I/O.
        var text = data.TryGetText();
        if (!string.IsNullOrWhiteSpace(text))
        {
            var candidates = text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(p => Path.IsPathFullyQualified(p) && (File.Exists(p) || Directory.Exists(p)))
                .ToArray();
            if (candidates.Length > 0)
            {
                paths = candidates;
                copy = true;
                return true;
            }
        }

        return false;
    }

    private sealed class Payload
    {
        public bool Copy { get; set; }
        public string[] Paths { get; set; } = [];
    }
}
