// Copyright (c) 2026 Hub-code-develop. Licensed under the MIT License.
//
// Bridges the UI-free FileInspector to the preview surface: opens the entry through the VFS
// provider (so archive and network entries work), converts fields to bindable objects, and
// degrades to "no preview" instead of throwing when a file is corrupt.

using FileHub.Desktop.ViewModels;
using FileHub.Files.Models;
using FileHub.Files.Services;
using FileHub.Files.Vfs;

namespace FileHub.Desktop.Services;

public sealed class InspectionResult
{
    public IReadOnlyList<PreviewField> Fields { get; init; } = [];
    public string? Text { get; init; }
    public string? Notice { get; init; }
    public byte[]? ThumbnailBytes { get; init; }
}

public static class FileInspectionService
{
    public static async Task<InspectionResult> InspectAsync(FileEntry model, CancellationToken ct = default)
    {
        try
        {
            var path = model.FullPath;
            var inspection = await FileInspector.InspectAsync(
                path, model.Size,
                token => VfsFileSystem.Default.OpenReadAsync(path, token),
                ct).ConfigureAwait(false);

            return new InspectionResult
            {
                Fields = inspection.Fields
                    .Where(f => !string.IsNullOrWhiteSpace(f.Value))
                    .Select(f => new PreviewField(f.Label, f.Value))
                    .ToList(),
                Text = inspection.Text,
                Notice = inspection.Notice,
                ThumbnailBytes = inspection.ThumbnailBytes
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException or InvalidDataException
                                       or ArgumentException)
        {
            return new InspectionResult();
        }
    }
}
