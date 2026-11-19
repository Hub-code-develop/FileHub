// Copyright (c) 2026 Hub-code-develop. Licensed under the MIT License.
//
// Preview capability detection and text extraction. This project is logic-only and must not
// depend on Avalonia: image bytes are handed to the desktop layer, which owns decoding.

using System.Text;
using FileHub.Files.Models;

namespace FileHub.Files.Services;

/// <summary>The kind of inline preview an entry can produce.</summary>
public enum PreviewKind
{
    None,
    Image,
    Text
}

/// <summary>
/// Cross-platform preview probing. Files App builds thumbnails through WinRT
/// (StorageFile.GetThumbnailAsync); that API does not exist on macOS or Linux, so FileHub
/// detects the format here and lets the platform-agnostic image decoder in the desktop layer
/// do the work. See THIRD-PARTY-NOTICES.md for the attribution note.
/// </summary>
public static class PreviewService
{
    // Formats the Skia-backed Avalonia decoder can turn into a bitmap without extra packages.
    // SVG needs Avalonia.Svg and HEIC needs platform codecs; those stay on the icon fallback.
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".xml", ".yml", ".yaml", ".cs", ".xaml", ".axaml",
        ".py", ".js", ".ts", ".jsx", ".tsx", ".java", ".c", ".cpp", ".h", ".hpp",
        ".sh", ".ps1", ".bat", ".sql", ".log", ".csv", ".ini", ".cfg", ".toml",
        ".properties", ".gradle", ".gitignore", ".rtf", ".css", ".scss", ".html", ".htm"
    };

    public static PreviewKind GetKind(FileEntry entry)
    {
        if (entry.IsDirectory) return PreviewKind.None;
        var ext = entry.Extension;
        if (ImageExtensions.Contains(ext)) return PreviewKind.Image;
        if (TextExtensions.Contains(ext)) return PreviewKind.Text;
        return PreviewKind.None;
    }

    public static bool CanShowImage(string path)
        => ImageExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Reads a bounded prefix of a text file. Binary files (NUL bytes) are rejected instead of
    /// showing mojibake, and only the first 8 KiB is examined so huge logs stay cheap.
    /// </summary>
    public static Task<string?> ReadTextPreviewAsync(
        string path, int maxCharacters = 4000, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite, 8192, FileOptions.SequentialScan);
                var buffer = new byte[8192];
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) return null;

                // A NUL byte in the sampled window is a reliable enough binary signal.
                if (Array.IndexOf(buffer, (byte)0, 0, read) >= 0) return null;

                var text = new UTF8Encoding(false, false).GetString(buffer, 0, read)
                    .TrimStart('\uFEFF');
                if (text.Length > maxCharacters) text = text[..maxCharacters] + "\n...";
                return text.Length == 0 ? null : text;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }, cancellationToken);
    }
}
