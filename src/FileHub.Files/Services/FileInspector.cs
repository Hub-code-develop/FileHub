// Copyright (c) 2026 Hub-code-develop. Licensed under the MIT License.
//
// Structural file inspection for the preview panel. Everything here is pure logic: bytes arrive
// through an injected open delegate so the same code serves local files, archive entries and
// remote streams, and nothing depends on Avalonia. The format parsers are hand written (no PDF
// or media libraries are pulled in) and every read is bounds checked; malformed input degrades
// to fewer fields or a Notice, and never throws for content reasons.

using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace FileHub.Files.Services;

/// <summary>A single label/value row shown in the inspection sheet.</summary>
public sealed class InspectionField
{
    /// <summary>Creates an empty field; used by object initialisers.</summary>
    public InspectionField()
    {
        Label = string.Empty;
        Value = string.Empty;
    }

    /// <summary>Creates a field with the given label and value.</summary>
    /// <param name="label">Row caption, for example "Duration".</param>
    /// <param name="value">Formatted value, for example "3:41".</param>
    public InspectionField(string label, string value)
    {
        Label = label;
        Value = value;
    }

    /// <summary>Row caption.</summary>
    public string Label;

    /// <summary>Formatted value.</summary>
    public string Value;
}

/// <summary>Broad category of content detected for a file.</summary>
public enum InspectionKind
{
    /// <summary>No inspection is available for this entry.</summary>
    None,

    /// <summary>Plain text or source code.</summary>
    Text,

    /// <summary>Raster image.</summary>
    Image,

    /// <summary>PDF document.</summary>
    Pdf,

    /// <summary>Audio container or elementary stream.</summary>
    Audio,

    /// <summary>Video container.</summary>
    Video,

    /// <summary>TrueType or OpenType font.</summary>
    Font,

    /// <summary>Markdown source, with a rendered HTML fragment.</summary>
    Markdown,

    /// <summary>Archive container such as zip or tar.</summary>
    Archive,

    /// <summary>Unrecognised bytes.</summary>
    Binary,

    /// <summary>Office Open XML document or legacy OLE2 Office file.</summary>
    Office
}

/// <summary>Result of inspecting one file: metadata rows plus optional preview payloads.</summary>
public sealed class FileInspection
{
    /// <summary>Detected category. Drives which preview presenter the shell picks.</summary>
    public InspectionKind Kind = InspectionKind.None;

    /// <summary>
    /// Ordered metadata rows, two to twelve entries. The first row is always a human readable
    /// format description and the last row is the file size.
    /// </summary>
    public IReadOnlyList<InspectionField> Fields = Array.Empty<InspectionField>();

    /// <summary>Leading source text for text-like files, at most 4000 characters; otherwise null.</summary>
    public string? Text;

    /// <summary>Minimal escaped HTML for Markdown; null for every other kind.</summary>
    public string? HtmlFragment;

    /// <summary>
    /// Small embedded picture decoded from the file itself: an ID3 APIC frame in MP3 or the
    /// MP4 "covr" atom. Null when the format has no cheap thumbnail to extract.
    /// </summary>
    public byte[]? ThumbnailBytes;

    /// <summary>Human readable caveat such as "Encrypted PDF"; null when nothing notable applies.</summary>
    public string? Notice;
}

/// <summary>
/// Format sniffing and structural parsing for the preview panel. All parsing works on a bounded
/// in-memory copy of the file so it behaves the same for local paths, archive entries and remote
/// streams (the delegate hides whether the source is seekable).
/// </summary>
public static class FileInspector
{
    private const int DefaultReadLimit = 8 * 1024 * 1024;
    private const int PdfReadLimit = 16 * 1024 * 1024;
    private const int ThumbnailLimit = 1024 * 1024;
    private const int TextPreviewLimit = 4000;
    private const int MaxFields = 12;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jpe", ".gif", ".bmp", ".webp", ".tif", ".tiff"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".m4a", ".m4b", ".aac", ".ogg", ".oga", ".opus", ".aiff", ".aif"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".flv", ".mpg", ".mpeg", ".3gp"
    };

    private static readonly HashSet<string> FontExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ttf", ".otf", ".ttc", ".otc"
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".jar", ".war", ".tar", ".tgz", ".gz", ".bz2", ".tbz2", ".xz", ".txz",
        ".7z", ".rar", ".zst"
    };

    private static readonly HashSet<string> OfficeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".docm", ".xlsx", ".xlsm", ".pptx", ".pptm", ".doc", ".xls", ".ppt"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".json", ".jsonc", ".xml", ".yml", ".yaml", ".toml", ".ini", ".cfg",
        ".conf", ".cs", ".xaml", ".axaml", ".py", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx",
        ".java", ".kt", ".c", ".h", ".cpp", ".hpp", ".cc", ".csproj", ".props", ".targets",
        ".sh", ".bash", ".zsh", ".ps1", ".bat", ".cmd", ".sql", ".csv", ".tsv", ".css", ".scss",
        ".html", ".htm", ".sln", ".slnx", ".gradle", ".gitignore", ".editorconfig", ".rtf"
    };

    // MPEG audio bitrate tables in kbit/s, indexed by the 4-bit bitrate index.
    private static readonly int[] BitrateV1L1 = { 0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448 };
    private static readonly int[] BitrateV1L2 = { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384 };
    private static readonly int[] BitrateV1L3 = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 };
    private static readonly int[] BitrateV2L1 = { 0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256 };
    private static readonly int[] BitrateV2L2 = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 };

    /// <summary>
    /// Determines the inspection category from the path alone. No IO is performed, which keeps
    /// the preview presenter cheap to build for thousands of entries.
    /// </summary>
    /// <param name="path">File name or virtual path; only the final extension is considered.</param>
    /// <returns>The category the inspector will attempt.</returns>
    public static InspectionKind KindOf(string path)
    {
        if (string.IsNullOrEmpty(path)) return InspectionKind.Binary;

        string ext = Path.GetExtension(path);
        if (ext.Length < 2) return InspectionKind.Binary;
        string e = ext.ToLowerInvariant();

        if (e == ".pdf") return InspectionKind.Pdf;
        if (ImageExtensions.Contains(e)) return InspectionKind.Image;
        if (AudioExtensions.Contains(e)) return InspectionKind.Audio;
        if (VideoExtensions.Contains(e)) return InspectionKind.Video;
        if (FontExtensions.Contains(e)) return InspectionKind.Font;
        if (e is ".md" or ".markdown" or ".mdown" or ".mkd") return InspectionKind.Markdown;
        if (ArchiveExtensions.Contains(e)) return InspectionKind.Archive;
        if (OfficeExtensions.Contains(e)) return InspectionKind.Office;
        if (TextExtensions.Contains(e)) return InspectionKind.Text;
        return InspectionKind.Binary;
    }

    /// <summary>
    /// Inspects a file and returns metadata, and for text/Markdown the leading source text.
    /// </summary>
    /// <param name="path">Path used for naming and extension sniffing (may be a virtual path).</param>
    /// <param name="size">Total size in bytes; used for limits and the size row.</param>
    /// <param name="openRead">Opens the content for reading. The returned stream need not be seekable.</param>
    /// <param name="ct">Cancellation token; cancellation is always observed and propagated.</param>
    /// <returns>A populated inspection; unreadable or malformed input yields a degraded result.</returns>
    public static Task<FileInspection> InspectAsync(
        string path,
        long size,
        Func<CancellationToken, Task<Stream>> openRead,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(openRead);
        return InspectCoreAsync(path ?? string.Empty, size, openRead, ct);
    }

    private static async Task<FileInspection> InspectCoreAsync(
        string path, long size, Func<CancellationToken, Task<Stream>> openRead, CancellationToken ct)
    {
        InspectionKind kind = KindOf(path);
        if (kind == InspectionKind.None)
        {
            return new FileInspection { Kind = InspectionKind.None };
        }

        int limit = kind == InspectionKind.Pdf ? PdfReadLimit : DefaultReadLimit;
        byte[] data;
        bool truncated;
        try
        {
            BoundedRead read = await ReadBoundedAsync(openRead, limit, ct).ConfigureAwait(false);
            data = read.Data;
            truncated = read.Truncated;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsExpectedReadFailure(ex))
        {
            var failed = new Draft(kind);
            failed.Add("Format", DescribeKind(kind));
            failed.Add("Problem", ex.Message);
            failed.Notes.Add("Content could not be read");
            failed.AddSize(size);
            return failed.Build();
        }

        var draft = new Draft(kind);
        if (truncated)
        {
            draft.Notes.Add(string.Format(CultureInfo.InvariantCulture,
                "Inspected first {0} of {1}", FormatBytes(limit), FormatBytes(size)));
        }

        try
        {
            switch (kind)
            {
                case InspectionKind.Pdf:
                    ParsePdf(data, draft);
                    break;
                case InspectionKind.Audio:
                    ParseAudio(data, draft, path);
                    break;
                case InspectionKind.Video:
                    ParseVideo(data, draft, path);
                    break;
                case InspectionKind.Font:
                    ParseFont(data, draft);
                    break;
                case InspectionKind.Markdown:
                    ParseMarkdown(data, draft);
                    break;
                case InspectionKind.Archive:
                    ParseArchive(data, draft, path);
                    break;
                case InspectionKind.Office:
                    ParseOffice(data, draft, path);
                    break;
                case InspectionKind.Image:
                    ParseImage(data, draft, path);
                    break;
                case InspectionKind.Text:
                    ParseText(data, draft);
                    break;
                default:
                    ParseBinary(data, draft);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsExpectedReadFailure(ex))
        {
            // Defensive: a parser bug or an exotic layout must never take the preview panel down.
            draft.Notes.Add("Partial parse: " + ex.Message);
            if (draft.Fields.Count == 0) draft.Add("Format", DescribeKind(kind));
        }

        draft.AddSize(size);
        return draft.Build();
    }

    // ---------------------------------------------------------------- reading

    private readonly record struct BoundedRead(byte[] Data, bool Truncated);

    private static async Task<BoundedRead> ReadBoundedAsync(
        Func<CancellationToken, Task<Stream>> openRead, int limit, CancellationToken ct)
    {
        // Copy into memory so non-seekable streams (SFTP, SMB, archive entries) behave identically.
        Stream source = await openRead(ct).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            long total = 0;
            while (total < limit)
            {
                ct.ThrowIfCancellationRequested();
                int want = (int)Math.Min(chunk.Length, limit - total);
                int read = await source.ReadAsync(chunk.AsMemory(0, want), ct).ConfigureAwait(false);
                if (read <= 0) break;
                buffer.Write(chunk, 0, read);
                total += read;
            }

            bool truncated = false;
            if (total >= limit)
            {
                var probe = new byte[1];
                int extra = await source.ReadAsync(probe.AsMemory(0, 1), ct).ConfigureAwait(false);
                truncated = extra > 0;
            }

            return new BoundedRead(buffer.ToArray(), truncated);
        }
    }

    private static bool IsExpectedReadFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidDataException
            or ArgumentException or NotSupportedException or ObjectDisposedException
            or FormatException or OverflowException;

    // ---------------------------------------------------------------- draft

    private sealed class Draft
    {
        private readonly InspectionKind _kind;

        public Draft(InspectionKind kind)
        {
            _kind = kind;
        }

        public List<InspectionField> Fields { get; } = new();
        public List<string> Notes { get; } = new();
        public string? Text;
        public string? Html;
        public byte[]? Thumbnail;

        public void Add(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            Fields.Add(new InspectionField(label, value.Trim()));
        }

        public void AddSize(long size)
        {
            Add("Size", string.Format(CultureInfo.InvariantCulture, "{0} ({1} bytes)",
                FormatBytes(size), size.ToString(CultureInfo.InvariantCulture)));
        }

        public FileInspection Build()
        {
            if (Fields.Count == 0) Add("Format", DescribeKind(_kind));
            if (Fields.Count == 1) Add("Size", "unknown");

            IReadOnlyList<InspectionField> fields = Fields;
            if (fields.Count > MaxFields)
            {
                // Keep the specific rows and preserve the trailing size row.
                var trimmed = fields.Take(MaxFields - 1).ToList();
                trimmed.Add(fields[^1]);
                fields = trimmed;
            }

            return new FileInspection
            {
                Kind = _kind,
                Fields = fields,
                Text = Text,
                HtmlFragment = Html,
                ThumbnailBytes = Thumbnail,
                Notice = Notes.Count == 0 ? null : string.Join("; ", Notes)
            };
        }
    }

    // ---------------------------------------------------------------- text

    private static void ParseText(byte[] data, Draft d)
    {
        int sample = Math.Min(data.Length, 8192);
        if (ContainsNul(data, sample))
        {
            d.Add("Format", "Text (binary control bytes present)");
            d.Add("Encoding", "unknown");
            d.Notes.Add("File contains NUL bytes");
            return;
        }

        string encodingName;
        int bom;
        Encoding encoding = DetectEncoding(data, out encodingName, out bom);
        string text;
        try
        {
            text = encoding.GetString(data, bom, data.Length - bom).TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException)
        {
            encodingName = "Latin-1 (fallback)";
            text = Encoding.Latin1.GetString(data, bom, data.Length - bom);
        }

        int lines = 1;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') lines++;
        }

        d.Add("Format", "Plain text");
        d.Add("Encoding", encodingName);
        d.Add("Lines", lines.ToString(CultureInfo.InvariantCulture) + (text.Length < data.Length ? "+" : string.Empty));
        d.Add("Characters", text.Length.ToString(CultureInfo.InvariantCulture) + (text.Length < data.Length ? "+" : string.Empty));

        if (text.Length > TextPreviewLimit)
        {
            d.Text = text[..TextPreviewLimit];
            d.Notes.Add("Preview shows the first 4000 characters");
        }
        else
        {
            d.Text = text;
        }
    }

    private static bool ContainsNul(byte[] data, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (data[i] == 0) return true;
        }
        return false;
    }

    private static Encoding DetectEncoding(byte[] data, out string name, out int bomLength)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
        {
            name = "UTF-8 with BOM";
            bomLength = 3;
            return new UTF8Encoding(false, true);
        }

        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
        {
            name = "UTF-16 LE";
            bomLength = 2;
            return new UnicodeEncoding(false, false, true);
        }

        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
        {
            name = "UTF-16 BE";
            bomLength = 2;
            return new UnicodeEncoding(true, false, true);
        }

        name = "UTF-8";
        bomLength = 0;
        return new UTF8Encoding(false, true);
    }

    // ---------------------------------------------------------------- markdown

    private static void ParseMarkdown(byte[] data, Draft d)
    {
        string source = new UTF8Encoding(false, false).GetString(data).TrimStart('\uFEFF');
        if (ContainsNul(data, Math.Min(data.Length, 4096)))
        {
            ParseBinary(data, d);
            d.Notes.Add("Markdown file looks binary");
            return;
        }

        int headings = 0;
        foreach (string line in EnumerateLines(source))
        {
            if (line.StartsWith('#')) headings++;
        }

        d.Add("Format", "Markdown");
        d.Add("Words", CountWords(source).ToString(CultureInfo.InvariantCulture));
        d.Add("Headings", headings.ToString(CultureInfo.InvariantCulture));
        d.Add("Characters", source.Length.ToString(CultureInfo.InvariantCulture));

        string visible = source.Length > TextPreviewLimit ? source[..TextPreviewLimit] : source;
        d.Text = visible;
        if (visible.Length < source.Length) d.Notes.Add("Preview shows the first 4000 characters");
        d.Html = RenderMarkdown(visible);
    }

    private static IEnumerable<string> EnumerateLines(string text)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            int end = i > start && text[i - 1] == '\r' ? i - 1 : i;
            yield return text[start..end];
            start = i + 1;
        }
        if (start <= text.Length - 1 || text.Length == 0)
        {
            yield return start <= text.Length - 1 ? text[start..] : string.Empty;
        }
    }

    private static int CountWords(string text)
    {
        int words = 0;
        bool inWord = false;
        foreach (char c in text)
        {
            bool space = char.IsWhiteSpace(c);
            if (!space && !inWord)
            {
                words++;
                inWord = true;
            }
            else if (space)
            {
                inWord = false;
            }
        }
        return words;
    }

    /// <summary>
    /// Renders a deliberately small Markdown subset to HTML. Every character is escaped first, so
    /// raw HTML in the source can never reach the preview surface as markup or script.
    /// </summary>
    private static string RenderMarkdown(string source)
    {
        var sb = new StringBuilder(source.Length * 2);
        var paragraph = new StringBuilder();
        var code = new StringBuilder();
        bool inCode = false;
        string? listTag = null;

        void FlushParagraph()
        {
            if (paragraph.Length == 0) return;
            sb.Append("<p>").Append(RenderInline(paragraph.ToString())).Append("</p>\n");
            paragraph.Clear();
        }

        void CloseList()
        {
            if (listTag is null) return;
            sb.Append("</").Append(listTag).Append(">\n");
            listTag = null;
        }

        foreach (string rawLine in EnumerateLines(source))
        {
            string line = rawLine.TrimEnd();
            string trimmed = line.TrimStart();

            if (inCode)
            {
                if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
                {
                    sb.Append("<pre><code>").Append(EscapeHtml(code.ToString())).Append("</code></pre>\n");
                    code.Clear();
                    inCode = false;
                }
                else
                {
                    code.Append(line).Append('\n');
                }
                continue;
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                FlushParagraph();
                CloseList();
                inCode = true;
                continue;
            }

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                CloseList();
                continue;
            }

            if (trimmed is "---" or "***" or "___")
            {
                FlushParagraph();
                CloseList();
                sb.Append("<hr />\n");
                continue;
            }

            int level = 0;
            while (level < trimmed.Length && level < 6 && trimmed[level] == '#') level++;
            if (level > 0 && level < trimmed.Length && trimmed[level] == ' ')
            {
                FlushParagraph();
                CloseList();
                string headingText = trimmed[(level + 1)..];
                sb.Append("<h").Append(level.ToString(CultureInfo.InvariantCulture)).Append('>')
                    .Append(RenderInline(headingText))
                    .Append("</h").Append(level.ToString(CultureInfo.InvariantCulture)).Append(">\n");
                continue;
            }

            if (trimmed.StartsWith("> ", StringComparison.Ordinal) || trimmed == ">")
            {
                FlushParagraph();
                CloseList();
                sb.Append("<blockquote><p>")
                    .Append(RenderInline(trimmed.Length > 2 ? trimmed[2..] : string.Empty))
                    .Append("</p></blockquote>\n");
                continue;
            }

            if (TryListItem(trimmed, out string marker, out string itemText))
            {
                FlushParagraph();
                string tag = marker is "-" or "*" or "+" ? "ul" : "ol";
                if (listTag != tag)
                {
                    CloseList();
                    sb.Append('<').Append(tag).Append(">\n");
                    listTag = tag;
                }
                sb.Append("<li>").Append(RenderInline(itemText)).Append("</li>\n");
                continue;
            }

            CloseList();
            if (paragraph.Length > 0) paragraph.Append(' ');
            paragraph.Append(trimmed);
        }

        if (inCode && code.Length > 0)
        {
            sb.Append("<pre><code>").Append(EscapeHtml(code.ToString())).Append("</code></pre>\n");
        }

        FlushParagraph();
        CloseList();
        return sb.ToString().TrimEnd();
    }

    private static bool TryListItem(string trimmed, out string marker, out string text)
    {
        marker = string.Empty;
        text = string.Empty;
        if (trimmed.Length < 3) return false;

        if (trimmed[0] is '-' or '*' or '+')
        {
            if (trimmed[1] != ' ') return false;
            marker = trimmed[..1];
            text = trimmed[2..].Trim();
            return true;
        }

        int digits = 0;
        while (digits < trimmed.Length && char.IsAsciiDigit(trimmed[digits])) digits++;
        if (digits == 0 || digits + 1 >= trimmed.Length) return false;
        if (trimmed[digits] is not ('.' or ')') || trimmed[digits + 1] != ' ') return false;
        marker = trimmed[..(digits + 1)];
        text = trimmed[(digits + 2)..].Trim();
        return true;
    }

    private static string RenderInline(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];

            if (c == '`')
            {
                int close = text.IndexOf('`', i + 1);
                if (close > i + 1)
                {
                    sb.Append("<code>").Append(EscapeHtml(text[(i + 1)..close])).Append("</code>");
                    i = close + 1;
                    continue;
                }
            }

            if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int close = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (close > i + 2)
                {
                    sb.Append("<strong>").Append(RenderInline(text[(i + 2)..close])).Append("</strong>");
                    i = close + 2;
                    continue;
                }
            }

            if (c == '*')
            {
                int close = text.IndexOf('*', i + 1);
                if (close > i + 1 && !char.IsWhiteSpace(text[i + 1]))
                {
                    sb.Append("<em>").Append(RenderInline(text[(i + 1)..close])).Append("</em>");
                    i = close + 1;
                    continue;
                }
            }

            if (c == '[')
            {
                int labelEnd = text.IndexOf(']', i + 1);
                if (labelEnd > i && labelEnd + 1 < text.Length && text[labelEnd + 1] == '(')
                {
                    int urlEnd = text.IndexOf(')', labelEnd + 2);
                    if (urlEnd > labelEnd)
                    {
                        string label = text[(i + 1)..labelEnd];
                        string url = text[(labelEnd + 2)..urlEnd].Trim();
                        if (IsSafeUrl(url))
                        {
                            sb.Append("<a href=\"").Append(EscapeHtml(url)).Append("\">")
                                .Append(RenderInline(label)).Append("</a>");
                        }
                        else
                        {
                            sb.Append(RenderInline(label));
                        }
                        i = urlEnd + 1;
                        continue;
                    }
                }
            }

            AppendEscaped(sb, c);
            i++;
        }
        return sb.ToString();
    }

    private static bool IsSafeUrl(string url)
    {
        if (url.Length == 0) return false;
        if (url[0] is '#' or '/') return true;
        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
               || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
    }

    private static string EscapeHtml(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (char c in text) AppendEscaped(sb, c);
        return sb.ToString();
    }

    private static void AppendEscaped(StringBuilder sb, char c)
    {
        switch (c)
        {
            case '&': sb.Append("&amp;"); break;
            case '<': sb.Append("&lt;"); break;
            case '>': sb.Append("&gt;"); break;
            case '"': sb.Append("&quot;"); break;
            case '\'': sb.Append("&#39;"); break;
            default: sb.Append(c); break;
        }
    }

    // ---------------------------------------------------------------- pdf

    private static void ParsePdf(byte[] data, Draft d)
    {
        bool isPdf = StartsWith(data, "%PDF-"u8);
        string? version = null;
        if (isPdf)
        {
            int i = 5;
            var v = new StringBuilder();
            while (i < data.Length && v.Length < 8 && (char.IsAsciiDigit((char)data[i]) || data[i] == (byte)'.'))
            {
                v.Append((char)data[i]);
                i++;
            }
            if (v.Length > 0) version = v.ToString();
        }

        d.Add("Format", version is null ? "PDF" : "PDF " + version);
        if (!isPdf) d.Notes.Add("Missing %PDF- header");

        ReadOnlySpan<byte> span = data;
        bool encrypted = span.IndexOf("/Encrypt"u8) >= 0;

        int pages = CountPdfPages(span);
        if (encrypted)
        {
            d.Add("Pages", pages > 0 ? pages.ToString(CultureInfo.InvariantCulture) + " (estimated)" : "unknown (encrypted)");
            d.Notes.Add("Encrypted PDF");
        }
        else if (pages > 0)
        {
            d.Add("Pages", pages.ToString(CultureInfo.InvariantCulture) + " (estimated)");
        }
        else
        {
            d.Add("Pages", "unknown");
            d.Notes.Add("No page objects found in the inspected bytes");
        }

        if (TryPdfInfo(span, "/Title"u8, out string title)) d.Add("Title", title);
        if (TryPdfInfo(span, "/Author"u8, out string author)) d.Add("Author", author);
        if (TryPdfInfo(span, "/Producer"u8, out string producer)) d.Add("Producer", producer);
        if (TryPdfInfo(span, "/Creator"u8, out string creator)) d.Add("Creator", creator);
        if (TryPdfInfo(span, "/CreationDate"u8, out string created)) d.Add("Created", FormatPdfDate(created));

        if (!encrypted && pages > 0 && span.IndexOf("/Font"u8) < 0)
        {
            d.Notes.Add("No font resources detected (possibly a scanned document)");
        }
    }

    /// <summary>
    /// Counts /Type /Page dictionaries. This is an approximation: pages that live inside
    /// compressed object streams are invisible to a byte scan, so the number is a lower bound
    /// for such files and is reported as "estimated".
    /// </summary>
    private static int CountPdfPages(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> typeMarker = "/Type"u8;
        int count = 0;
        int pos = 0;
        int guard = 0;

        while (pos < data.Length && guard++ < 200000)
        {
            int idx = data[pos..].IndexOf(typeMarker);
            if (idx < 0) break;

            int p = pos + idx + typeMarker.Length;
            p = SkipPdfWhite(data, p);
            if (p < data.Length && data[p] == (byte)'/')
            {
                int nameStart = p + 1;
                int nameEnd = nameStart;
                while (nameEnd < data.Length && IsPdfNameByte(data[nameEnd])) nameEnd++;
                if (data[nameStart..nameEnd].SequenceEqual("Page"u8)) count++;
                p = nameEnd;
            }

            pos = p > pos ? p : pos + 1;
        }

        if (count == 0)
        {
            // Producers that hide the type entry still leave the page object itself in the clear
            // often enough for a loose scan to help; delimiters keep /PageMode out of the count.
            ReadOnlySpan<byte> looseMarker = "/Page"u8;
            int i = 0;
            int looseGuard = 0;
            while (i < data.Length && looseGuard++ < 200000)
            {
                int j = data[i..].IndexOf(looseMarker);
                if (j < 0) break;
                int after = i + j + looseMarker.Length;
                if (after >= data.Length || IsPdfDelimiterOrWhite(data[after])) count++;
                i = after;
            }
        }

        return count;
    }

    private static int SkipPdfWhite(ReadOnlySpan<byte> data, int pos)
    {
        while (pos < data.Length && IsPdfWhite(data[pos])) pos++;
        return pos;
    }

    private static bool IsPdfWhite(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;

    private static bool IsPdfDelimiterOrWhite(byte b) =>
        IsPdfWhite(b) || b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'['
            or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    private static bool IsPdfNameByte(byte b) => !IsPdfDelimiterOrWhite(b);

    private static bool TryPdfInfo(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key, out string value)
    {
        value = string.Empty;
        int pos = 0;
        int guard = 0;

        while (pos < data.Length && guard++ < 64)
        {
            int idx = data[pos..].IndexOf(key);
            if (idx < 0) return false;

            int p = pos + idx + key.Length;
            if (p < data.Length && !IsPdfDelimiterOrWhite(data[p]))
            {
                // A longer name such as /TitleX: keep looking.
                pos = p;
                continue;
            }

            p = SkipPdfWhite(data, p);
            if (p >= data.Length) return false;

            if (data[p] == (byte)'(')
            {
                if (TryReadPdfLiteral(data, ref p, out value) && value.Length > 0) return true;
                pos = p + 1;
                continue;
            }

            if (data[p] == (byte)'<')
            {
                if (TryReadPdfHex(data, ref p, out value) && value.Length > 0) return true;
                pos = p + 1;
                continue;
            }

            // Indirect reference ("12 0 R") or another token: skip and try the next match.
            pos = p + 1;
        }

        return false;
    }

    private static bool TryReadPdfLiteral(ReadOnlySpan<byte> data, ref int pos, out string value)
    {
        value = string.Empty;
        if (pos >= data.Length || data[pos] != (byte)'(') return false;

        var bytes = new List<byte>(32);
        int depth = 0;
        int i = pos;

        while (i < data.Length)
        {
            byte b = data[i];
            if (b == (byte)'\\' && i + 1 < data.Length)
            {
                byte esc = data[i + 1];
                switch (esc)
                {
                    case (byte)'n': bytes.Add(10); i += 2; continue;
                    case (byte)'r': bytes.Add(13); i += 2; continue;
                    case (byte)'t': bytes.Add(9); i += 2; continue;
                    case (byte)'b': bytes.Add(8); i += 2; continue;
                    case (byte)'f': bytes.Add(12); i += 2; continue;
                    case (byte)'(':
                    case (byte)')':
                    case (byte)'\\':
                        bytes.Add(esc);
                        i += 2;
                        continue;
                    case (byte)'\n': i += 2; continue;
                    case (byte)'\r':
                        i += data[i + 1] == (byte)'\n' ? 3 : 2;
                        continue;
                    default:
                        if (esc is >= (byte)'0' and <= (byte)'7')
                        {
                            int oct = 0;
                            int n = 0;
                            while (n < 3 && i + 1 + n < data.Length &&
                                   data[i + 1 + n] is >= (byte)'0' and <= (byte)'7')
                            {
                                oct = oct * 8 + (data[i + 1 + n] - '0');
                                n++;
                            }
                            bytes.Add((byte)(oct & 0xFF));
                            i += 1 + n;
                            continue;
                        }
                        bytes.Add(esc);
                        i += 2;
                        continue;
                }
            }

            if (b == (byte)'(')
            {
                depth++;
                if (depth == 1)
                {
                    i++;
                    continue;
                }
            }
            else if (b == (byte)')')
            {
                depth--;
                if (depth == 0)
                {
                    pos = i + 1;
                    value = DecodePdfBytes(bytes);
                    return true;
                }
            }

            bytes.Add(b);
            i++;
        }

        return false;
    }

    private static bool TryReadPdfHex(ReadOnlySpan<byte> data, ref int pos, out string value)
    {
        value = string.Empty;
        if (pos >= data.Length || data[pos] != (byte)'<') return false;

        var bytes = new List<byte>(16);
        int i = pos + 1;
        int high = -1;

        while (i < data.Length)
        {
            byte b = data[i];
            if (b == (byte)'>')
            {
                if (high >= 0) bytes.Add((byte)(high << 4));
                pos = i + 1;
                value = DecodePdfBytes(bytes);
                return true;
            }

            int nibble = HexValue(b);
            if (nibble >= 0)
            {
                if (high < 0)
                {
                    high = nibble;
                }
                else
                {
                    bytes.Add((byte)((high << 4) | nibble));
                    high = -1;
                }
            }

            i++;
        }

        return false;
    }

    private static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1
    };

    private static string DecodePdfBytes(List<byte> bytes)
    {
        if (bytes.Count >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes.ToArray(), 2, bytes.Count - 2).TrimEnd('\0');
        }
        if (bytes.Count >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes.ToArray(), 2, bytes.Count - 2).TrimEnd('\0');
        }
        return Encoding.Latin1.GetString(bytes.ToArray()).TrimEnd('\0');
    }

    private static string FormatPdfDate(string raw)
    {
        // D:YYYYMMDDHHmmSS+HH'mm'
        string s = raw.StartsWith("D:", StringComparison.Ordinal) ? raw[2..] : raw;
        if (s.Length >= 14 &&
            s.AsSpan(0, 14).ToString().All(char.IsAsciiDigit))
        {
            string formatted = string.Format(CultureInfo.InvariantCulture,
                "{0}-{1}-{2} {3}:{4}:{5}", s[..4], s[4..6], s[6..8], s[8..10], s[10..12], s[12..14]);
            return formatted;
        }
        if (s.Length >= 8 && s.AsSpan(0, 8).ToString().All(char.IsAsciiDigit))
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}-{1}-{2}", s[..4], s[4..6], s[6..8]);
        }
        return raw;
    }

    // ---------------------------------------------------------------- audio

    private static void ParseAudio(byte[] data, Draft d, string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();

        if (StartsWith(data, "ID3"u8) || StartsWith(data, new byte[] { 0xFF, 0xFB }) ||
            StartsWith(data, new byte[] { 0xFF, 0xF3 }) || StartsWith(data, new byte[] { 0xFF, 0xF2 }) ||
            ext == ".mp3")
        {
            ParseMp3(data, d);
            return;
        }

        if (StartsWith(data, "fLaC"u8))
        {
            ParseFlac(data, d);
            return;
        }

        if (StartsWith(data, "RIFF"u8) && data.Length >= 12 && data.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            ParseWav(data, d);
            return;
        }

        if (StartsWith(data, "OggS"u8))
        {
            ParseOgg(data, d);
            return;
        }

        if (StartsWith(data, "FORM"u8) && data.Length >= 12 &&
            (data.AsSpan(8, 4).SequenceEqual("AIFF"u8) || data.AsSpan(8, 4).SequenceEqual("AIFC"u8)))
        {
            ParseAiff(data, d);
            return;
        }

        if (data.Length >= 12 && data.AsSpan(4, 4).SequenceEqual("ftyp"u8))
        {
            ParseMp4(data, d, isVideo: false);
            return;
        }

        if (data.Length >= 2 && data[0] == 0xFF && (data[1] & 0xF6) == 0xF0)
        {
            ParseAdts(data, d);
            return;
        }

        d.Add("Format", "Audio");
        d.Notes.Add("Container not recognised");
        ParseBinaryHead(data, d);
    }

    private static void ParseMp3(byte[] data, Draft d)
    {
        int audioStart = 0;
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        byte[]? artwork = null;

        if (data.Length >= 10 && StartsWith(data, "ID3"u8))
        {
            int major = data[3];
            int flags = data[5];
            int tagSize = SyncSafe(data, 6);
            int bodyStart = 10;
            int bodyEnd = Math.Min(data.Length, bodyStart + tagSize);

            byte[] body = data;
            if ((flags & 0x80) != 0)
            {
                body = Deunsync(data, bodyStart, bodyEnd);
                bodyStart = 0;
                bodyEnd = body.Length;
            }

            if (major is 3 or 4)
            {
                ReadId3Frames(body, bodyStart, bodyEnd, major == 4, tags, ref artwork);
                d.Add("Tags", "ID3v2." + major.ToString(CultureInfo.InvariantCulture));
            }
            else if (major == 2)
            {
                d.Add("Tags", "ID3v2.2 (not parsed)");
            }

            audioStart = Math.Min(data.Length, 10 + tagSize);
        }

        int frame = FindMpegFrame(data, audioStart);
        if (frame < 0)
        {
            d.Add("Format", "MPEG audio");
            d.Notes.Add("No MPEG frame header found");
            AddTags(d, tags);
            if (artwork is not null) d.Thumbnail = artwork;
            return;
        }

        uint header = ReadU32BE(data, frame);
        int versionBits = (int)((header >> 19) & 3);
        int layerBits = (int)((header >> 17) & 3);
        int bitrateIndex = (int)((header >> 12) & 0xF);
        int rateIndex = (int)((header >> 10) & 3);
        int padding = (int)((header >> 9) & 1);
        int channelMode = (int)((header >> 6) & 3);
        bool hasCrc = ((header >> 16) & 1) == 0;

        (string versionName, int[]? rates) = versionBits switch
        {
            3 => ("MPEG-1", new[] { 44100, 48000, 32000 }),
            2 => ("MPEG-2", new[] { 22050, 24000, 16000 }),
            0 => ("MPEG-2.5", new[] { 11025, 12000, 8000 }),
            _ => ("MPEG-2", new[] { 22050, 24000, 16000 })
        };

        int[]? bitrates = (versionBits, layerBits) switch
        {
            (3, 3) => BitrateV1L1,
            (3, 2) => BitrateV1L2,
            (3, 1) => BitrateV1L3,
            (2, 3) => BitrateV2L1,
            (2, 2) => BitrateV2L2,
            (2, 1) => BitrateV2L2,
            (0, 3) => BitrateV2L1,
            (0, 2) => BitrateV2L2,
            (0, 1) => BitrateV2L2,
            _ => BitrateV1L3
        };

        int sampleRate = rateIndex < 3 ? rates[rateIndex] : 44100;
        int bitrate = bitrateIndex is > 0 and < 15 ? bitrates[bitrateIndex] : 0;
        string layerName = layerBits switch { 3 => "Layer I", 2 => "Layer II", _ => "Layer III" };
        int samplesPerFrame = layerBits == 3 ? 384 : layerBits == 2 ? 1152 : (versionBits == 3 ? 1152 : 576);

        int channels = channelMode == 3 ? 1 : 2;
        d.Add("Format", versionName + " " + layerName);
        if (bitrate > 0) d.Add("Bitrate", bitrate.ToString(CultureInfo.InvariantCulture) + " kbps");
        if (sampleRate > 0) d.Add("Sample rate", sampleRate.ToString(CultureInfo.InvariantCulture) + " Hz");
        d.Add("Channels", channels == 1 ? "Mono" : "Stereo");

        int sideInfo = versionBits == 3 ? (channels == 1 ? 17 : 32) : (channels == 1 ? 9 : 17);
        int xingOffset = frame + 4 + (hasCrc ? 2 : 0) + sideInfo;
        double duration = 0;
        bool vbr = false;

        if (xingOffset + 12 <= data.Length)
        {
            ReadOnlySpan<byte> tag = data.AsSpan(xingOffset, 4);
            if (tag.SequenceEqual("Xing"u8) || tag.SequenceEqual("Info"u8))
            {
                vbr = tag.SequenceEqual("Xing"u8);
                uint flags = ReadU32BE(data, xingOffset + 4);
                int cursor = xingOffset + 8;
                if ((flags & 1) != 0 && cursor + 4 <= data.Length)
                {
                    uint frames = ReadU32BE(data, cursor);
                    if (sampleRate > 0 && frames > 0)
                    {
                        duration = (double)frames * samplesPerFrame / sampleRate;
                    }
                }
            }
        }

        if (duration <= 0 && bitrate > 0)
        {
            long audioBytes = Math.Max(0, data.Length - frame);
            duration = audioBytes * 8.0 / (bitrate * 1000.0);
        }

        if (duration > 0) d.Add("Duration", FormatDuration(duration));
        if (vbr) d.Add("Mode", "VBR (Xing)");

        AddTags(d, tags);
        if (artwork is not null && artwork.Length <= ThumbnailLimit) d.Thumbnail = artwork;
    }

    private static void ReadId3Frames(
        byte[] data, int start, int end, bool syncSafeSizes,
        Dictionary<string, string> tags, ref byte[]? artwork)
    {
        int pos = start;
        int guard = 0;

        while (pos + 10 <= end && guard++ < 512)
        {
            string id = Encoding.ASCII.GetString(data, pos, 4);
            if (id == "\0\0\0\0" || id.Any(c => !char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c)))
            {
                break;
            }

            int size = syncSafeSizes ? SyncSafe(data, pos + 4) : (int)ReadU32BE(data, pos + 4);
            if (size < 0 || pos + 10 + size > end) break;

            int payload = pos + 10;
            int payloadEnd = payload + size;

            switch (id)
            {
                case "TIT2":
                case "TPE1":
                case "TALB":
                case "TYER":
                case "TDRC":
                case "TCON":
                case "TPE2":
                {
                    string value = ReadId3Text(data, payload, payloadEnd);
                    if (value.Length > 0)
                    {
                        string key = id switch
                        {
                            "TIT2" => "Title",
                            "TPE1" => "Artist",
                            "TPE2" => "Album artist",
                            "TALB" => "Album",
                            "TYER" or "TDRC" => "Year",
                            _ => "Genre"
                        };
                        tags[key] = id == "TCON" ? value.Trim('(', ')') : value;
                    }
                    break;
                }
                case "APIC" when artwork is null:
                    artwork = TryReadId3Picture(data, payload, payloadEnd);
                    break;
            }

            pos = payloadEnd;
        }
    }

    private static string ReadId3Text(byte[] data, int start, int end)
    {
        if (start >= end) return string.Empty;
        byte encoding = data[start];
        int offset = start + 1;

        Encoding enc = encoding switch
        {
            1 => HasUtf16Bom(data, offset, end),
            2 => Encoding.BigEndianUnicode,
            0 => Encoding.Latin1,
            _ => new UTF8Encoding(false, false)
        };

        string text = enc.GetString(data, offset, end - offset);
        int nul = text.IndexOf('\0');
        if (nul >= 0) text = text[..nul];
        return text.Trim();
    }

    private static Encoding HasUtf16Bom(byte[] data, int offset, int end)
    {
        if (offset + 1 < end && data[offset] == 0xFF && data[offset + 1] == 0xFE) return Encoding.Unicode;
        return Encoding.BigEndianUnicode;
    }

    private static byte[]? TryReadId3Picture(byte[] data, int start, int end)
    {
        if (start + 4 > end) return null;
        int pos = start + 1;

        int mimeEnd = pos;
        while (mimeEnd < end && data[mimeEnd] != 0) mimeEnd++;
        string mime = Encoding.Latin1.GetString(data, pos, mimeEnd - pos);
        pos = mimeEnd + 1;

        if (pos >= end) return null;
        pos++; // picture type

        // Description is a NUL-terminated string in the frame encoding.
        if (pos < end)
        {
            byte encoding = data[start];
            bool wide = encoding is 1 or 2;
            if (wide)
            {
                while (pos + 1 < end && !(data[pos] == 0 && data[pos + 1] == 0)) pos += 2;
                pos += 2;
            }
            else
            {
                while (pos < end && data[pos] != 0) pos++;
                pos++;
            }
        }

        if (pos >= end) return null;
        int length = end - pos;
        if (length <= 8 || length > ThumbnailLimit) return null;

        bool image = data[pos] == 0xFF && data[pos + 1] == 0xD8
                     || data[pos] == 0x89 && data[pos + 1] == 0x50;
        if (!image) return null;
        if (!mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && mime.Length > 0) return null;

        var result = new byte[length];
        Array.Copy(data, pos, result, 0, length);
        return result;
    }

    private static byte[] Deunsync(byte[] data, int start, int end)
    {
        var result = new byte[Math.Max(0, end - start)];
        int o = 0;
        for (int i = start; i < end; i++)
        {
            result[o++] = data[i];
            if (data[i] == 0xFF && i + 1 < end && data[i + 1] == 0x00) i++;
        }
        if (o == result.Length) return result;
        return result.AsSpan(0, o).ToArray();
    }

    private static int FindMpegFrame(byte[] data, int start)
    {
        int limit = Math.Min(data.Length - 4, start + 512 * 1024);
        for (int i = Math.Max(0, start); i <= limit; i++)
        {
            if (data[i] != 0xFF) continue;
            byte b1 = data[i + 1];
            if ((b1 & 0xE0) != 0xE0) continue;
            int version = (b1 >> 3) & 3;
            int layer = (b1 >> 1) & 3;
            int bitrate = (data[i + 2] >> 4) & 0xF;
            int rate = (data[i + 2] >> 2) & 3;
            if (version == 1 || layer == 0 || bitrate == 15 || rate == 3) continue;
            return i;
        }
        return -1;
    }

    private static int SyncSafe(byte[] data, int offset)
    {
        if (offset + 4 > data.Length) return 0;
        return (data[offset] & 0x7F) << 21
               | (data[offset + 1] & 0x7F) << 14
               | (data[offset + 2] & 0x7F) << 7
               | (data[offset + 3] & 0x7F);
    }

    private static void ParseFlac(byte[] data, Draft d)
    {
        d.Add("Format", "FLAC");
        int pos = 4;
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        bool haveStream = false;
        int guard = 0;

        while (pos + 4 <= data.Length && guard++ < 64)
        {
            byte header = data[pos];
            bool last = (header & 0x80) != 0;
            int type = header & 0x7F;
            int length = (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
            int body = pos + 4;
            if (length < 0 || body + length > data.Length) break;

            if (type == 0 && length >= 18)
            {
                ulong packed = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(body + 10, 8));
                int sampleRate = (int)(packed >> 44);
                int channels = (int)((packed >> 41) & 0x7) + 1;
                int bits = (int)((packed >> 36) & 0x1F) + 1;
                long totalSamples = (long)(packed & 0xFFFFFFFFFUL);

                if (sampleRate > 0) d.Add("Sample rate", sampleRate.ToString(CultureInfo.InvariantCulture) + " Hz");
                d.Add("Bit depth", bits.ToString(CultureInfo.InvariantCulture) + " bit");
                d.Add("Channels", Channels(channels));
                if (sampleRate > 0 && totalSamples > 0)
                {
                    d.Add("Duration", FormatDuration((double)totalSamples / sampleRate));
                    d.Add("Samples", totalSamples.ToString(CultureInfo.InvariantCulture));
                }
                haveStream = true;
            }
            else if (type == 4)
            {
                ReadVorbisComments(data, body, body + length, tags);
            }

            pos = body + length;
            if (last) break;
        }

        if (!haveStream) d.Notes.Add("STREAMINFO block not found");
        AddTags(d, tags);
    }

    private static void ReadVorbisComments(byte[] data, int start, int end, Dictionary<string, string> tags)
    {
        if (start + 4 > end) return;
        int pos = start;
        int vendorLength = (int)ReadU32LE(data, pos);
        pos += 4;
        if (vendorLength < 0 || pos + vendorLength > end) return;
        pos += vendorLength;
        if (pos + 4 > end) return;

        int count = (int)ReadU32LE(data, pos);
        pos += 4;
        if (count < 0 || count > 4096) return;

        for (int i = 0; i < count && pos + 4 <= end; i++)
        {
            int length = (int)ReadU32LE(data, pos);
            pos += 4;
            if (length < 0 || pos + length > end) return;

            string entry = Encoding.UTF8.GetString(data, pos, length);
            pos += length;
            int eq = entry.IndexOf('=');
            if (eq <= 0) continue;

            string key = entry[..eq].ToUpperInvariant();
            string value = entry[(eq + 1)..].Trim();
            if (value.Length == 0) continue;

            switch (key)
            {
                case "TITLE": tags["Title"] = value; break;
                case "ARTIST": tags["Artist"] = value; break;
                case "ALBUM": tags["Album"] = value; break;
                case "DATE": tags["Year"] = value; break;
                case "GENRE": tags["Genre"] = value; break;
            }
        }
    }

    private static void ParseWav(byte[] data, Draft d)
    {
        d.Add("Format", "WAV");
        int pos = 12;
        int guard = 0;
        int channels = 0;
        int sampleRate = 0;
        int bits = 0;
        int byteRate = 0;
        long dataSize = 0;
        int formatTag = 0;

        while (pos + 8 <= data.Length && guard++ < 512)
        {
            string id = Encoding.ASCII.GetString(data, pos, 4);
            long size = ReadU32LE(data, pos + 4);
            int body = pos + 8;
            if (size < 0 || body + size > data.Length) size = Math.Max(0, data.Length - body);

            if (id == "fmt " && size >= 16)
            {
                formatTag = ReadU16LE(data, body);
                channels = ReadU16LE(data, body + 2);
                sampleRate = (int)ReadU32LE(data, body + 4);
                byteRate = (int)ReadU32LE(data, body + 8);
                bits = ReadU16LE(data, body + 14);
            }
            else if (id == "data")
            {
                dataSize = size;
            }

            pos = body + (int)size + ((int)size & 1);
        }

        string tagName = formatTag switch
        {
            1 => "PCM",
            3 => "IEEE float",
            6 => "A-law",
            7 => "mu-law",
            0x55 => "MP3",
            _ => formatTag == 0 ? "unknown" : "0x" + formatTag.ToString("X2", CultureInfo.InvariantCulture)
        };

        d.Add("Codec", tagName);
        if (sampleRate > 0) d.Add("Sample rate", sampleRate.ToString(CultureInfo.InvariantCulture) + " Hz");
        if (channels > 0) d.Add("Channels", Channels(channels));
        if (bits > 0) d.Add("Bit depth", bits.ToString(CultureInfo.InvariantCulture) + " bit");
        if (byteRate > 0) d.Add("Bitrate", ((byteRate * 8) / 1000).ToString(CultureInfo.InvariantCulture) + " kbps");

        double duration = byteRate > 0 && dataSize > 0 ? (double)dataSize / byteRate : 0;
        if (duration > 0) d.Add("Duration", FormatDuration(duration));
        if (dataSize > 0) d.Add("Audio data", FormatBytes(dataSize));
    }

    private static void ParseOgg(byte[] data, Draft d)
    {
        d.Add("Format", "Ogg");
        int channels = 0;
        int sampleRate = 0;
        int preSkip = 0;
        string codec = "unknown";

        // The first page's payload carries the identification header of the codec.
        int firstPayload = -1;
        if (data.Length > 27 && data[4] == 0)
        {
            int segments = data[26];
            int tableStart = 27;
            int payloadStart = tableStart + segments;
            if (payloadStart < data.Length)
            {
                int packetLength = 0;
                for (int i = 0; i < segments && tableStart + i < data.Length; i++)
                {
                    packetLength += data[tableStart + i];
                    if (data[tableStart + i] < 255) break;
                }
                if (packetLength > 0 && payloadStart + packetLength <= data.Length) firstPayload = payloadStart;
            }
        }

        if (firstPayload >= 0)
        {
            if (data.AsSpan(firstPayload, Math.Min(8, data.Length - firstPayload)).SequenceEqual("OpusHead"u8))
            {
                codec = "Opus";
                if (firstPayload + 19 <= data.Length)
                {
                    channels = data[firstPayload + 9];
                    preSkip = ReadU16LE(data, firstPayload + 10);
                    sampleRate = 48000; // Opus always decodes at 48 kHz.
                }
            }
            else if (firstPayload + 16 <= data.Length && data[firstPayload] == 1 &&
                     data.AsSpan(firstPayload + 1, 6).SequenceEqual("vorbis"u8))
            {
                codec = "Vorbis";
                channels = data[firstPayload + 11];
                sampleRate = (int)ReadU32LE(data, firstPayload + 12);
            }
        }

        d.Add("Codec", codec);
        if (channels > 0) d.Add("Channels", Channels(channels));
        if (sampleRate > 0) d.Add("Sample rate", sampleRate.ToString(CultureInfo.InvariantCulture) + " Hz");

        // The final page carries the absolute granule position; scanning backwards avoids
        // walking every page of a long stream.
        int lastPage = data.AsSpan().LastIndexOf("OggS"u8);
        if (lastPage >= 0 && lastPage + 14 <= data.Length)
        {
            long granule = (long)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(lastPage + 6, 8));
            if (granule > 0 && sampleRate > 0)
            {
                long samples = Math.Max(0, granule - preSkip);
                d.Add("Duration", FormatDuration((double)samples / sampleRate));
            }
        }

        if (codec == "unknown") d.Notes.Add("Codec header not found in the inspected bytes");
    }

    private static void ParseAiff(byte[] data, Draft d)
    {
        d.Add("Format", "AIFF");
        int pos = 12;
        int guard = 0;

        while (pos + 8 <= data.Length && guard++ < 256)
        {
            string id = Encoding.ASCII.GetString(data, pos, 4);
            long size = ReadU32BE(data, pos + 4);
            int body = pos + 8;
            if (size < 0 || body + size > data.Length) size = Math.Max(0, data.Length - body);

            if (id == "COMM" && size >= 18)
            {
                int channels = ReadU16BE(data, body);
                long frames = ReadU32BE(data, body + 2);
                int bits = ReadU16BE(data, body + 6);
                double rate = ReadExtended80(data, body + 8);

                if (rate > 0) d.Add("Sample rate", rate.ToString("0.###", CultureInfo.InvariantCulture) + " Hz");
                d.Add("Channels", Channels(channels));
                d.Add("Bit depth", bits.ToString(CultureInfo.InvariantCulture) + " bit");
                if (rate > 0 && frames > 0)
                {
                    d.Add("Duration", FormatDuration(frames / rate));
                }
                return;
            }

            pos = body + (int)size + ((int)size & 1);
        }

        d.Notes.Add("COMM chunk not found");
    }

    private static void ParseAdts(byte[] data, Draft d)
    {
        d.Add("Format", "AAC (ADTS)");
        if (data.Length < 7)
        {
            d.Notes.Add("Header truncated");
            return;
        }

        int[] rates = { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350 };
        int rateIndex = (data[2] >> 2) & 0xF;
        int channels = ((data[2] & 1) << 2) | ((data[3] >> 6) & 3);
        int sampleRate = rateIndex < rates.Length ? rates[rateIndex] : 44100;

        int frames = 0;
        int pos = 0;
        int guard = 0;
        while (pos + 7 <= data.Length && guard++ < 500000)
        {
            if (data[pos] != 0xFF || (data[pos + 1] & 0xF6) != 0xF0) break;
            int frameLength = ((data[pos + 3] & 3) << 11) | (data[pos + 4] << 3) | (data[pos + 5] >> 5);
            if (frameLength < 7) break;
            frames++;
            pos += frameLength;
        }

        d.Add("Sample rate", sampleRate.ToString(CultureInfo.InvariantCulture) + " Hz");
        if (channels > 0) d.Add("Channels", Channels(channels));
        if (frames > 0)
        {
            d.Add("Duration", FormatDuration((double)frames * 1024 / sampleRate));
            d.Add("Frames", frames.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            d.Notes.Add("Frame walk stopped early (truncated or resynced)");
        }
    }

    private static string Channels(int count) => count switch
    {
        1 => "Mono",
        2 => "Stereo",
        _ => count.ToString(CultureInfo.InvariantCulture) + " channels"
    };

    private static void AddTags(Draft d, Dictionary<string, string> tags)
    {
        foreach (string key in new[] { "Title", "Artist", "Album artist", "Album", "Year", "Genre" })
        {
            if (tags.TryGetValue(key, out string? value)) d.Add(key, value);
        }
    }

    // ---------------------------------------------------------------- mp4 / mov

    private sealed class Mp4Info
    {
        public string? Brand;
        public double Duration;
        public int Width;
        public int Height;
        public readonly Dictionary<string, string> Tags = new(StringComparer.Ordinal);
        public bool HasVideoTrack;
        public byte[]? Artwork;
    }

    private static void ParseVideo(byte[] data, Draft d, string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();

        if (data.Length >= 12 && data.AsSpan(4, 4).SequenceEqual("ftyp"u8))
        {
            ParseMp4(data, d, isVideo: true);
            return;
        }

        if (data.Length >= 4 && data[0] == 0x1A && data[1] == 0x45 && data[2] == 0xDF && data[3] == 0xA3)
        {
            ParseMatroska(data, d);
            return;
        }

        if (StartsWith(data, "RIFF"u8) && data.Length >= 12 && data.AsSpan(8, 4).SequenceEqual("AVI "u8))
        {
            ParseAvi(data, d);
            return;
        }

        d.Add("Format", "Video");
        d.Add("Container", "." + ext.TrimStart('.'));
        d.Notes.Add("Container not recognised; metadata not parsed");
    }

    private static void ParseMp4(byte[] data, Draft d, bool isVideo)
    {
        var info = new Mp4Info();
        ScanMp4(data, 0, data.Length, info, 0);

        string brand = info.Brand ?? "MP4";
        d.Add("Format", brand + (isVideo ? " video" : " audio"));

        if (info.Duration > 0) d.Add("Duration", FormatDuration(info.Duration));
        if (isVideo)
        {
            if (info.Width > 0 && info.Height > 0)
            {
                d.Add("Dimensions", info.Width.ToString(CultureInfo.InvariantCulture) + " x " +
                                     info.Height.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                d.Add("Dimensions", "unknown");
            }
            if (!info.HasVideoTrack) d.Notes.Add("No video track detected");
        }
        else
        {
            if (info.Width > 0 && info.Height > 0)
            {
                d.Add("Artwork size", info.Width.ToString(CultureInfo.InvariantCulture) + " x " +
                                      info.Height.ToString(CultureInfo.InvariantCulture));
            }
        }

        if (info.Artwork is not null) d.Thumbnail = info.Artwork;

        foreach (string[] pair in new[]
                 {
                     new[] { "Title", "\u00A9nam" },
                     new[] { "Artist", "\u00A9ART" },
                     new[] { "Album", "\u00A9alb" },
                     new[] { "Year", "\u00A9day" },
                     new[] { "Encoder", "\u00A9too" }
                 })
        {
            if (info.Tags.TryGetValue(pair[1], out string? value)) d.Add(pair[0], value);
        }
    }

    private static void ScanMp4(byte[] data, long start, long end, Mp4Info info, int depth)
    {
        if (depth > 10 || start < 0 || end > data.Length) return;

        long pos = start;
        int guard = 0;
        while (pos + 8 <= end && guard++ < 4096)
        {
            long size = ReadU32BE(data, (int)pos);
            string type = Encoding.Latin1.GetString(data, (int)pos + 4, 4);
            long header = 8;

            if (size == 1)
            {
                if (pos + 16 > end) return;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan((int)pos + 8, 8));
                header = 16;
            }
            else if (size == 0)
            {
                size = end - pos;
            }

            if (size < header || pos + size > end) return;

            long payload = pos + header;
            long boxEnd = pos + size;

            switch (type)
            {
                case "ftyp":
                    if (payload + 4 <= boxEnd)
                    {
                        string major = Encoding.Latin1.GetString(data, (int)payload, 4).Trim();
                        info.Brand = major switch
                        {
                            "isom" or "iso2" or "mp41" or "mp42" => "MP4",
                            "M4A " => "M4A",
                            "qt  " => "QuickTime",
                            "3gp4" or "3gp5" => "3GP",
                            _ => major.Length > 0 ? major : "MP4"
                        };
                    }
                    break;

                case "moov":
                case "trak":
                case "mdia":
                case "minf":
                case "stbl":
                case "udta":
                case "edts":
                case "dinf":
                    ScanMp4(data, payload, boxEnd, info, depth + 1);
                    break;

                case "meta":
                    // Full box: 4 bytes of version/flags precede the children.
                    ScanMp4(data, payload + 4, boxEnd, info, depth + 1);
                    break;

                case "ilst":
                    ScanMp4(data, payload, boxEnd, info, depth + 1);
                    break;

                case "mvhd":
                {
                    int p = (int)payload;
                    int version = data[p];
                    if (version == 1)
                    {
                        if (p + 32 <= boxEnd)
                        {
                            uint timescale = ReadU32BE(data, p + 20);
                            ulong duration = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(p + 24, 8));
                            if (timescale > 0) info.Duration = (double)duration / timescale;
                        }
                    }
                    else if (p + 20 <= boxEnd)
                    {
                        uint timescale = ReadU32BE(data, p + 12);
                        uint duration = ReadU32BE(data, p + 16);
                        if (timescale > 0) info.Duration = (double)duration / timescale;
                    }
                    break;
                }

                case "tkhd":
                {
                    int p = (int)payload;
                    int version = data[p];
                    int widthOffset = version == 1 ? 88 : 76;
                    if (p + widthOffset + 8 <= boxEnd)
                    {
                        uint w = ReadU32BE(data, p + widthOffset);
                        uint h = ReadU32BE(data, p + widthOffset + 4);
                        int iw = (int)(w >> 16);
                        int ih = (int)(h >> 16);
                        if (iw > 0 && ih > 0)
                        {
                            info.Width = Math.Max(info.Width, iw);
                            info.Height = Math.Max(info.Height, ih);
                            info.HasVideoTrack = true;
                        }
                    }
                    break;
                }

                case "hdlr":
                {
                    int p = (int)payload;
                    if (p + 12 <= boxEnd)
                    {
                        string handler = Encoding.Latin1.GetString(data, p + 8, 4);
                        if (handler == "vide") info.HasVideoTrack = true;
                    }
                    break;
                }

                case "\u00A9nam":
                case "\u00A9ART":
                case "\u00A9alb":
                case "\u00A9day":
                case "\u00A9too":
                {
                    string? text = ReadIlstText(data, payload, boxEnd);
                    if (!string.IsNullOrWhiteSpace(text)) info.Tags[type] = text!;
                    break;
                }

                case "covr" when info.Artwork is null:
                    info.Artwork = ReadIlstData(data, payload, boxEnd);
                    break;
            }

            pos = boxEnd;
        }
    }

    private static byte[]? ReadIlstData(byte[] data, long start, long end)
    {
        long pos = start;
        int guard = 0;
        while (pos + 8 <= end && guard++ < 8)
        {
            long size = ReadU32BE(data, (int)pos);
            string type = Encoding.Latin1.GetString(data, (int)pos + 4, 4);
            if (size < 8 || pos + size > end) return null;
            if (type == "data")
            {
                int payloadStart = (int)pos + 16;
                int length = (int)(pos + size) - payloadStart;
                if (length <= 8 || length > ThumbnailLimit) return null;
                bool image = data[payloadStart] == 0xFF && data[payloadStart + 1] == 0xD8
                             || data[payloadStart] == 0x89 && data[payloadStart + 1] == 0x50;
                if (!image) return null;
                var result = new byte[length];
                Array.Copy(data, payloadStart, result, 0, length);
                return result;
            }
            pos += size;
        }
        return null;
    }

    private static string? ReadIlstText(byte[] data, long start, long end)
    {
        long pos = start;
        int guard = 0;
        while (pos + 8 <= end && guard++ < 8)
        {
            long size = ReadU32BE(data, (int)pos);
            string type = Encoding.Latin1.GetString(data, (int)pos + 4, 4);
            if (size < 8 || pos + size > end) return null;
            if (type == "data" && pos + 16 <= end)
            {
                // version/flags then locale then the UTF-8 payload.
                int textStart = (int)pos + 16;
                int textLength = (int)(pos + size) - textStart;
                if (textLength <= 0) return null;
                return Encoding.UTF8.GetString(data, textStart, textLength).Trim('\0', ' ', '\n', '\r');
            }
            pos += size;
        }
        return null;
    }

    // ---------------------------------------------------------------- matroska

    private sealed class EbmlState
    {
        public string? DocType;
        public long TimecodeScale = 1_000_000;
        public double DurationTicks = double.NaN;
        public int Width;
        public int Height;
    }

    private static void ParseMatroska(byte[] data, Draft d)
    {
        var state = new EbmlState();
        ScanEbml(data, 0, data.Length, state, 0);

        d.Add("Format", (state.DocType ?? "Matroska").ToUpperInvariant());
        if (!double.IsNaN(state.DurationTicks) && state.DurationTicks > 0 && state.TimecodeScale > 0)
        {
            double seconds = state.DurationTicks * state.TimecodeScale / 1_000_000_000.0;
            d.Add("Duration", FormatDuration(seconds));
        }
        else
        {
            d.Notes.Add("Duration not found in the inspected bytes");
        }

        if (state.Width > 0 && state.Height > 0)
        {
            d.Add("Dimensions", state.Width.ToString(CultureInfo.InvariantCulture) + " x " +
                                 state.Height.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void ScanEbml(byte[] data, long start, long end, EbmlState state, int depth)
    {
        if (depth > 12 || start < 0 || end > data.Length || start >= end) return;

        long pos = start;
        int guard = 0;

        while (pos < end && guard++ < 100000)
        {
            if (!ReadEbmlVint(data, ref pos, end, keepMarker: true, out ulong id, out _)) return;
            if (!ReadEbmlVint(data, ref pos, end, keepMarker: false, out ulong size, out int sizeLength)) return;

            bool unknownSize = size == (1UL << (7 * sizeLength)) - 1;
            long payload = pos;
            long payloadEnd = unknownSize ? end : payload + (long)size;
            if (payloadEnd < payload || payloadEnd > end) return;

            switch (id)
            {
                case 0x18538067: // Segment
                case 0x1A45DFA3: // EBML header
                case 0x1549A966: // Info
                case 0x1654AE6B: // Tracks
                case 0xAE:       // TrackEntry
                case 0xE0:       // Video
                    ScanEbml(data, payload, payloadEnd, state, depth + 1);
                    break;

                case 0x4282: // DocType
                    if (size <= 64)
                    {
                        state.DocType = Encoding.ASCII.GetString(data, (int)payload, (int)size).TrimEnd('\0');
                    }
                    break;

                case 0x2AD7B1: // TimecodeScale
                    state.TimecodeScale = ReadEbmlUInt(data, payload, size);
                    break;

                case 0x4489: // Duration, a float in segment ticks
                    state.DurationTicks = ReadEbmlFloat(data, payload, size);
                    break;

                case 0xB0: // PixelWidth
                    state.Width = (int)ReadEbmlUInt(data, payload, size);
                    break;

                case 0xBA: // PixelHeight
                    state.Height = (int)ReadEbmlUInt(data, payload, size);
                    break;

                case 0x1F43B675: // Cluster: skip frames entirely
                    break;
            }

            pos = payloadEnd;
        }
    }

    private static bool ReadEbmlVint(byte[] data, ref long pos, long end, bool keepMarker, out ulong value, out int length)
    {
        value = 0;
        length = 0;
        if (pos < 0 || pos >= end || pos >= data.Length) return false;

        byte first = data[pos];
        int len = 1;
        byte mask = 0x80;
        while (len <= 8 && (first & mask) == 0)
        {
            mask >>= 1;
            len++;
        }
        if (len > 8 || pos + len > end || pos + len > data.Length) return false;

        ulong v = keepMarker ? first : (ulong)(first & (mask - 1));
        for (int i = 1; i < len; i++)
        {
            v = (v << 8) | data[pos + i];
        }

        pos += len;
        value = v;
        length = len;
        return true;
    }

    private static long ReadEbmlUInt(byte[] data, long offset, ulong size)
    {
        int count = (int)Math.Min(size, 8);
        long value = 0;
        for (int i = 0; i < count; i++)
        {
            long at = offset + i;
            if (at < 0 || at >= data.Length) return value;
            value = (value << 8) | data[at];
        }
        return value;
    }

    private static double ReadEbmlFloat(byte[] data, long offset, ulong size)
    {
        if (offset < 0 || offset + (long)size > data.Length) return double.NaN;
        if (size == 4) return BinaryPrimitives.ReadSingleBigEndian(data.AsSpan((int)offset, 4));
        if (size == 8) return BinaryPrimitives.ReadDoubleBigEndian(data.AsSpan((int)offset, 8));
        return double.NaN;
    }

    private static void ParseAvi(byte[] data, Draft d)
    {
        d.Add("Format", "AVI");
        int microSecPerFrame = 0;
        int totalFrames = 0;
        int width = 0;
        int height = 0;

        int avih = data.AsSpan().IndexOf("avih"u8);
        if (avih >= 0 && avih + 20 <= data.Length)
        {
            microSecPerFrame = (int)ReadU32LE(data, avih + 8);
            totalFrames = (int)ReadU32LE(data, avih + 24);
            width = (int)ReadU32LE(data, avih + 40);
            height = (int)ReadU32LE(data, avih + 44);
        }

        if (width > 0 && height > 0)
        {
            d.Add("Dimensions", width.ToString(CultureInfo.InvariantCulture) + " x " +
                                 height.ToString(CultureInfo.InvariantCulture));
        }
        if (microSecPerFrame > 0 && totalFrames > 0)
        {
            double seconds = (double)microSecPerFrame * totalFrames / 1_000_000.0;
            d.Add("Duration", FormatDuration(seconds));
            d.Add("Frame rate", (1_000_000.0 / microSecPerFrame).ToString("0.##", CultureInfo.InvariantCulture) + " fps");
        }
        else
        {
            d.Notes.Add("AVI header not found");
        }
    }

    // ---------------------------------------------------------------- fonts

    private static void ParseFont(byte[] data, Draft d)
    {
        int baseOffset = 0;
        bool collection = StartsWith(data, "ttcf"u8);

        if (collection)
        {
            d.Add("Format", "TrueType Collection");
            if (data.Length >= 16)
            {
                int count = (int)ReadU32BE(data, 8);
                d.Add("Fonts in collection", count.ToString(CultureInfo.InvariantCulture));
                if (data.Length >= 12)
                {
                    uint first = ReadU32BE(data, 12);
                    if (first > 0 && first < data.Length) baseOffset = (int)first;
                }
            }
        }
        else if (StartsWith(data, "OTTO"u8))
        {
            d.Add("Format", "OpenType (CFF)");
        }
        else if (StartsWith(data, new byte[] { 0x00, 0x01, 0x00, 0x00 }) || StartsWith(data, "true"u8))
        {
            d.Add("Format", "TrueType");
        }
        else if (StartsWith(data, "wOFF"u8) || StartsWith(data, "wOF2"u8))
        {
            d.Add("Format", "Web font (compressed)");
            d.Notes.Add("WOFF containers are not parsed");
            return;
        }
        else
        {
            d.Add("Format", "Font (unknown signature)");
            ParseBinaryHead(data, d);
            return;
        }

        if (baseOffset + 12 > data.Length)
        {
            d.Notes.Add("Font header truncated");
            return;
        }

        int numTables = ReadU16BE(data, baseOffset + 4);
        var tables = new Dictionary<string, (int Offset, int Length)>(StringComparer.Ordinal);

        for (int i = 0; i < numTables && i < 512; i++)
        {
            int record = baseOffset + 12 + i * 16;
            if (record + 16 > data.Length) break;
            string tag = Encoding.ASCII.GetString(data, record, 4);
            int offset = (int)ReadU32BE(data, record + 8);
            int length = (int)ReadU32BE(data, record + 12);
            if (offset < 0 || length < 0 || offset + (long)length > data.Length) continue;
            tables[tag] = (offset, length);
        }

        if (tables.TryGetValue("head", out var head) && head.Length >= 54)
        {
            int unitsPerEm = ReadU16BE(data, head.Offset + 18);
            if (unitsPerEm > 0) d.Add("Units per em", unitsPerEm.ToString(CultureInfo.InvariantCulture));
        }

        if (tables.TryGetValue("maxp", out var maxp) && maxp.Length >= 6)
        {
            int glyphs = ReadU16BE(data, maxp.Offset + 4);
            if (glyphs > 0) d.Add("Glyphs", glyphs.ToString(CultureInfo.InvariantCulture));
        }

        if (tables.TryGetValue("name", out var name))
        {
            ReadFontNames(data, name.Offset, name.Length, d);
        }

        if (tables.TryGetValue("cmap", out var cmap))
        {
            int codepoints = CountCmapCodepoints(data, cmap.Offset, cmap.Length);
            if (codepoints > 0) d.Add("Codepoints", codepoints.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void ReadFontNames(byte[] data, int start, int length, Draft d)
    {
        int end = start + length;
        if (start + 6 > end) return;

        int count = ReadU16BE(data, start + 2);
        int stringOffset = ReadU16BE(data, start + 4);
        if (stringOffset <= 0 || stringOffset > length) return;

        var wanted = new Dictionary<int, string>
        {
            [0] = "Copyright",
            [1] = "Family",
            [2] = "Style",
            [5] = "Version"
        };
        var found = new Dictionary<int, string>();
        int bestScore = int.MinValue;

        for (int i = 0; i < count && i < 1024; i++)
        {
            int record = start + 6 + i * 12;
            if (record + 12 > end) break;

            int platform = ReadU16BE(data, record);
            int encoding = ReadU16BE(data, record + 2);
            int language = ReadU16BE(data, record + 4);
            int nameId = ReadU16BE(data, record + 6);
            int stringLength = ReadU16BE(data, record + 8);
            int offset = ReadU16BE(data, record + 10);

            if (!wanted.ContainsKey(nameId) || stringLength == 0) continue;

            int absolute = start + stringOffset + offset;
            if (absolute < 0 || absolute + stringLength > end) continue;

            // Prefer Windows US English, then Windows, then Unicode platform records.
            int score = platform == 3 && language == 0x409 ? 3 : platform == 3 ? 2 : platform == 0 ? 1 : 0;
            if (score <= 0) continue;

            string text = platform == 3 || platform == 0
                ? Encoding.BigEndianUnicode.GetString(data, absolute, stringLength & ~1)
                : Encoding.Latin1.GetString(data, absolute, stringLength);

            text = text.Trim('\0', ' ').Trim();
            if (text.Length == 0) continue;

            if (score > bestScore)
            {
                bestScore = score;
                found.Clear();
            }
            if (score == bestScore) found[nameId] = text;
        }

        if (found.TryGetValue(1, out string? family)) d.Add("Family", family);
        if (found.TryGetValue(2, out string? style)) d.Add("Style", style);
        if (found.TryGetValue(5, out string? version)) d.Add("Version", version);
        if (found.TryGetValue(0, out string? copyright)) d.Add("Copyright", copyright);
    }

    private static int CountCmapCodepoints(byte[] data, int start, int length)
    {
        int end = start + length;
        if (start + 4 > end) return 0;

        int tableCount = ReadU16BE(data, start + 2);
        int bestOffset = -1;
        int bestFormat = 0;
        int bestScore = -1;

        for (int i = 0; i < tableCount && i < 64; i++)
        {
            int record = start + 4 + i * 8;
            if (record + 8 > end) break;
            int platform = ReadU16BE(data, record);
            int encoding = ReadU16BE(data, record + 2);
            int offset = (int)ReadU32BE(data, record + 4);
            int subtable = start + offset;
            if (subtable + 4 > end) continue;

            int format = ReadU16BE(data, subtable);
            if (format is not (4 or 12)) continue;

            int score = platform == 3 && encoding == 10 ? 3
                : platform == 3 && encoding == 1 ? 2
                : platform == 0 ? 1
                : 0;
            if (score > bestScore)
            {
                bestScore = score;
                bestOffset = subtable;
                bestFormat = format;
            }
        }

        if (bestOffset < 0) return 0;

        if (bestFormat == 4)
        {
            int segCount = ReadU16BE(data, bestOffset + 6) / 2;
            if (segCount <= 0 || bestOffset + 14 + segCount * 2 > end) return 0;
            int total = 0;
            for (int i = 0; i < segCount; i++)
            {
                int endCode = ReadU16BE(data, bestOffset + 14 + i * 2);
                int startCode = ReadU16BE(data, bestOffset + 16 + segCount * 2 + i * 2);
                if (endCode == 0xFFFF || endCode < startCode) continue;
                total += endCode - startCode + 1;
            }
            return total;
        }

        int groups = (int)ReadU32BE(data, bestOffset + 12);
        if (groups <= 0 || groups > 100000) return 0;
        long totalCodes = 0;
        for (int i = 0; i < groups; i++)
        {
            int group = bestOffset + 16 + i * 12;
            if (group + 12 > end) break;
            long startChar = ReadU32BE(data, group);
            long endChar = ReadU32BE(data, group + 4);
            if (endChar < startChar || endChar - startChar > 0x110000) continue;
            totalCodes += endChar - startChar + 1;
        }
        return totalCodes > int.MaxValue ? int.MaxValue : (int)totalCodes;
    }

    // ---------------------------------------------------------------- images

    private static void ParseImage(byte[] data, Draft d, string path)
    {
        if (data.Length >= 8 && StartsWith(data, new byte[] { 0x89, 0x50, 0x4E, 0x47 }))
        {
            ParsePng(data, d);
            return;
        }

        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            ParseJpeg(data, d);
            return;
        }

        if (StartsWith(data, "GIF8"u8))
        {
            ParseGif(data, d);
            return;
        }

        if (StartsWith(data, "BM"u8) && data.Length >= 26)
        {
            int width = ReadI32LE(data, 18);
            int height = Math.Abs(ReadI32LE(data, 22));
            int bits = ReadU16LE(data, 28);
            d.Add("Format", "BMP");
            d.Add("Dimensions", width.ToString(CultureInfo.InvariantCulture) + " x " +
                                 height.ToString(CultureInfo.InvariantCulture));
            if (bits > 0) d.Add("Bit depth", bits.ToString(CultureInfo.InvariantCulture) + " bit");
            return;
        }

        if (StartsWith(data, "RIFF"u8) && data.Length >= 12 && data.AsSpan(8, 4).SequenceEqual("WEBP"u8))
        {
            ParseWebp(data, d);
            return;
        }

        if ((StartsWith(data, new byte[] { 0x49, 0x49, 0x2A, 0x00 }) ||
             StartsWith(data, new byte[] { 0x4D, 0x4D, 0x00, 0x2A })) && data.Length >= 8)
        {
            var tiff = new TiffReader(data, 0);
            var ifd0 = tiff.ReadIfd(tiff.FirstIfdOffset);
            int width = (int)(TiffLong(tiff, ifd0, 256) ?? 0);
            int height = (int)(TiffLong(tiff, ifd0, 257) ?? 0);
            d.Add("Format", "TIFF");
            if (width > 0 && height > 0)
            {
                d.Add("Dimensions", width.ToString(CultureInfo.InvariantCulture) + " x " +
                                     height.ToString(CultureInfo.InvariantCulture));
            }
            return;
        }

        d.Add("Format", "Image");
        d.Add("File type", "." + Path.GetExtension(path).TrimStart('.').ToLowerInvariant());
        d.Notes.Add("Image header not recognised");
    }

    private static void ParsePng(byte[] data, Draft d)
    {
        d.Add("Format", "PNG");
        if (data.Length < 33 || !data.AsSpan(12, 4).SequenceEqual("IHDR"u8))
        {
            d.Notes.Add("IHDR chunk not found");
            return;
        }

        int width = (int)ReadU32BE(data, 16);
        int height = (int)ReadU32BE(data, 20);
        int bitDepth = data[24];
        int colorType = data[25];
        int interlace = data[28];

        d.Add("Dimensions", width.ToString(CultureInfo.InvariantCulture) + " x " +
                             height.ToString(CultureInfo.InvariantCulture));
        d.Add("Bit depth", bitDepth.ToString(CultureInfo.InvariantCulture) + " bit");

        string color = colorType switch
        {
            0 => "Grayscale",
            2 => "Truecolor (RGB)",
            3 => "Indexed",
            4 => "Grayscale + alpha",
            6 => "Truecolor + alpha (RGBA)",
            _ => "Unknown"
        };
        d.Add("Color", color);
        if (interlace != 0) d.Add("Interlaced", "Adam7");
    }

    private static void ParseJpeg(byte[] data, Draft d)
    {
        d.Add("Format", "JPEG");
        int pos = 2;
        int guard = 0;
        int width = 0;
        int height = 0;
        int components = 0;
        TiffReader? exif = null;

        while (pos + 4 <= data.Length && guard++ < 4096)
        {
            if (data[pos] != 0xFF)
            {
                pos++;
                continue;
            }

            byte marker = data[pos + 1];
            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7) || marker == 0xFF)
            {
                pos += marker == 0xFF ? 1 : 2;
                continue;
            }

            if (marker == 0xD9 || marker == 0xDA) break;
            if (pos + 4 > data.Length) break;

            int segmentLength = (data[pos + 2] << 8) | data[pos + 3];
            if (segmentLength < 2 || pos + 2 + segmentLength > data.Length) break;

            if (IsStartOfFrame(marker))
            {
                int payload = pos + 4;
                if (payload + 6 <= data.Length)
                {
                    height = (data[payload + 1] << 8) | data[payload + 2];
                    width = (data[payload + 3] << 8) | data[payload + 4];
                    components = data[payload + 5];
                }
            }
            else if (marker == 0xE1 && pos + 8 <= data.Length &&
                     data.AsSpan(pos + 4, 6).SequenceEqual("Exif\0\0"u8))
            {
                exif = new TiffReader(data, pos + 10);
            }

            pos += 2 + segmentLength;
        }

        if (width > 0 && height > 0)
        {
            d.Add("Dimensions", width.ToString(CultureInfo.InvariantCulture) + " x " +
                                 height.ToString(CultureInfo.InvariantCulture));
        }
        if (components > 0)
        {
            string color = components switch { 1 => "Grayscale", 3 => "YCbCr", 4 => "CMYK", _ => components + " components" };
            d.Add("Color", color);
        }

        if (exif is not null && exif.IsValid)
        {
            var ifd0 = exif.ReadIfd(exif.FirstIfdOffset);
            var sub = ifd0.TryGetValue(0x8769, out TiffEntry entry)
                ? exif.ReadIfd((long)entry.Value)
                : new Dictionary<ushort, TiffEntry>();

            string? make = TiffString(exif, ifd0, 0x010F);
            string? model = TiffString(exif, ifd0, 0x0110);
            string camera = string.Join(' ', new[] { make, model }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (camera.Length > 0) d.Add("Camera", camera);

            string? taken = TiffString(exif, sub, 0x9003);
            if (taken is not null) d.Add("Taken", NormalizeExifDate(taken));

            double? exposure = TiffRational(exif, sub, 0x829A);
            if (exposure is > 0)
            {
                d.Add("Exposure", exposure >= 1
                    ? exposure.Value.ToString("0.#", CultureInfo.InvariantCulture) + " s"
                    : "1/" + Math.Round(1.0 / exposure.Value).ToString("0", CultureInfo.InvariantCulture) + " s");
            }

            double? aperture = TiffRational(exif, sub, 0x829D);
            if (aperture is > 0)
            {
                d.Add("Aperture", "f/" + aperture.Value.ToString("0.#", CultureInfo.InvariantCulture));
            }

            long? iso = TiffLong(exif, sub, 0x8827) ?? TiffLong(exif, ifd0, 0x8827);
            if (iso is > 0) d.Add("ISO", iso.Value.ToString(CultureInfo.InvariantCulture));

            double? focal = TiffRational(exif, sub, 0x920A);
            if (focal is > 0)
            {
                d.Add("Focal length", focal.Value.ToString("0.#", CultureInfo.InvariantCulture) + " mm");
            }

            long? orientation = TiffLong(exif, ifd0, 0x0112);
            if (orientation is > 1 and <= 8)
            {
                d.Add("Orientation", orientation.Value switch
                {
                    2 => "Mirrored horizontally",
                    3 => "Rotated 180",
                    4 => "Mirrored vertically",
                    5 => "Mirrored and rotated 90",
                    6 => "Rotated 90 clockwise",
                    7 => "Mirrored and rotated 270",
                    _ => "Rotated 270 clockwise"
                });
            }
        }
    }

    private static bool IsStartOfFrame(byte marker) =>
        marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;

    /// <summary>Turns the EXIF "YYYY:MM:DD HH:mm:ss" stamp into an ISO-like form.</summary>
    private static string NormalizeExifDate(string value)
    {
        if (value.Length >= 19 && value[4] == ':' && value[7] == ':')
        {
            return value[..10].Replace(':', '-') + " " + value[11..19];
        }
        return value;
    }

    private static void ParseGif(byte[] data, Draft d)
    {
        d.Add("Format", StartsWith(data, "GIF89a"u8) ? "GIF89a" : "GIF87a");
        if (data.Length < 10) return;
        int width = ReadU16LE(data, 6);
        int height = ReadU16LE(data, 8);
        d.Add("Dimensions", width.ToString(CultureInfo.InvariantCulture) + " x " +
                             height.ToString(CultureInfo.InvariantCulture));
        if (data.AsSpan().IndexOf("NETSCAPE2.0"u8) >= 0) d.Add("Animation", "Yes (loop extension)");
    }

    private static void ParseWebp(byte[] data, Draft d)
    {
        d.Add("Format", "WebP");
        if (data.Length < 16) return;

        string chunk = Encoding.ASCII.GetString(data, 12, 4);
        if (chunk == "VP8X" && data.Length >= 30)
        {
            int width = 1 + (data[24] | (data[25] << 8) | (data[26] << 16));
            int height = 1 + (data[27] | (data[28] << 8) | (data[29] << 16));
            d.Add("Dimensions", width.ToString(CultureInfo.InvariantCulture) + " x " +
                                 height.ToString(CultureInfo.InvariantCulture));
            d.Add("Mode", "Extended (VP8X)");
            return;
        }

        if (chunk == "VP8 " && data.Length >= 30)
        {
            int width = (ReadU16LE(data, 26) & 0x3FFF);
            int height = (ReadU16LE(data, 28) & 0x3FFF);
            d.Add("Dimensions", width.ToString(CultureInfo.InvariantCulture) + " x " +
                                 height.ToString(CultureInfo.InvariantCulture));
            d.Add("Mode", "Lossy");
            return;
        }

        d.Add("Mode", chunk.Trim());
    }

    // ---------------------------------------------------------------- tiff / exif

    private readonly struct TiffEntry
    {
        public TiffEntry(int type, int count, uint value)
        {
            Type = type;
            Count = count;
            Value = value;
        }

        public int Type { get; }
        public int Count { get; }
        public uint Value { get; }
    }

    private sealed class TiffReader
    {
        private readonly byte[] _data;
        private readonly int _start;

        public TiffReader(byte[] data, int start)
        {
            _data = data;
            _start = start;
            IsValid = false;
            LittleEndian = true;

            if (start < 0 || start + 8 > data.Length) return;
            if (data[start] == 0x49 && data[start + 1] == 0x49) LittleEndian = true;
            else if (data[start] == 0x4D && data[start + 1] == 0x4D) LittleEndian = false;
            else return;

            if (U16(start + 2) != 42) return;
            FirstIfdOffset = U32(start + 4);
            IsValid = FirstIfdOffset > 0 && start + FirstIfdOffset + 2 <= data.Length;
        }

        public bool IsValid { get; }
        public bool LittleEndian { get; }
        public uint FirstIfdOffset { get; }

        /// <summary>Backing buffer, exposed for the ASCII/rational readers.</summary>
        public byte[] Data => _data;

        /// <summary>Length of the backing buffer.</summary>
        public int DataLength => _data.Length;

        public ushort U16(long absolute)
        {
            if (absolute < 0 || absolute + 2 > _data.Length) return 0;
            int i = (int)absolute;
            return LittleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(i, 2))
                : BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(i, 2));
        }

        public uint U32(long absolute)
        {
            if (absolute < 0 || absolute + 4 > _data.Length) return 0;
            int i = (int)absolute;
            return LittleEndian
                ? BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(i, 4))
                : BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(i, 4));
        }

        public Dictionary<ushort, TiffEntry> ReadIfd(long relativeOffset)
        {
            var result = new Dictionary<ushort, TiffEntry>();
            long ifd = _start + relativeOffset;
            if (relativeOffset <= 0 || ifd + 2 > _data.Length) return result;

            int count = U16(ifd);
            if (count <= 0 || count > 4096) return result;

            for (int i = 0; i < count; i++)
            {
                long entry = ifd + 2 + i * 12;
                if (entry + 12 > _data.Length) break;
                ushort tag = U16(entry);
                int type = U16(entry + 2);
                int items = (int)U32(entry + 4);
                uint value = U32(entry + 8);
                result[tag] = new TiffEntry(type, items, value);
            }

            return result;
        }

        public long ValueOffset(TiffEntry entry)
        {
            int unit = entry.Type switch
            {
                1 or 2 or 6 or 7 => 1,
                3 or 8 => 2,
                4 or 9 or 11 => 4,
                5 or 10 or 12 => 8,
                _ => 1
            };

            long total = (long)unit * Math.Max(entry.Count, 1);
            return total <= 4 ? -1 : _start + entry.Value;
        }
    }

    private static string? TiffString(TiffReader tiff, Dictionary<ushort, TiffEntry> ifd, ushort tag)
    {
        if (!ifd.TryGetValue(tag, out TiffEntry entry) || entry.Count == 0) return null;
        long offset = tiff.ValueOffset(entry);
        if (offset < 0) return null;
        long length = Math.Min(entry.Count, 256);
        if (offset + length > tiff.DataLength) return null;
        string text = Encoding.UTF8.GetString(tiff.Data, (int)offset, (int)length).Trim('\0', ' ');
        return text.Length == 0 ? null : text;
    }

    private static long? TiffLong(TiffReader tiff, Dictionary<ushort, TiffEntry> ifd, ushort tag)
    {
        if (!ifd.TryGetValue(tag, out TiffEntry entry)) return null;
        if (entry.Type == 3)
        {
            long offset = tiff.ValueOffset(entry);
            return offset < 0 ? (entry.Value & 0xFFFF) : tiff.U16(offset);
        }
        if (entry.Type == 4)
        {
            long offset = tiff.ValueOffset(entry);
            return offset < 0 ? entry.Value : tiff.U32(offset);
        }
        return null;
    }

    private static double? TiffRational(TiffReader tiff, Dictionary<ushort, TiffEntry> ifd, ushort tag)
    {
        if (!ifd.TryGetValue(tag, out TiffEntry entry)) return null;
        if (entry.Type is not (5 or 10)) return null;
        long offset = tiff.ValueOffset(entry);
        if (offset < 0) return null;
        uint numerator = tiff.U32(offset);
        uint denominator = tiff.U32(offset + 4);
        if (denominator == 0) return null;
        double value = (double)numerator / denominator;
        if (entry.Type == 10)
        {
            int n = (int)numerator;
            int d = (int)denominator;
            if (d != 0) value = (double)n / d;
        }
        return value;
    }

    // ---------------------------------------------------------------- office

    private static void ParseOffice(byte[] data, Draft d, string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();

        if (StartsWith(data, new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }))
        {
            string legacy = ext switch
            {
                ".doc" => "Word 97-2003 document",
                ".xls" => "Excel 97-2003 workbook",
                ".ppt" => "PowerPoint 97-2003 presentation",
                _ => "OLE2 compound document"
            };
            d.Add("Format", legacy);
            d.Add("Container", "OLE2 / CFB");
            d.Notes.Add("Legacy binary Office metadata is not parsed; save as OOXML for detail");
            return;
        }

        if (!IsZip(data))
        {
            d.Add("Format", OfficeName(ext) + " (unreadable container)");
            d.Notes.Add("Expected a zip container");
            return;
        }

        using var zip = TryOpenZip(data);
        if (zip is null)
        {
            d.Add("Format", OfficeName(ext) + " (corrupt container)");
            d.Notes.Add("Zip directory could not be read");
            return;
        }

        d.Add("Format", OfficeName(ext));

        string? core = ReadZipText(zip, "docProps/core.xml", 64 * 1024);
        if (core is not null)
        {
            XDocument? doc = TryParseXml(core);
            if (doc is not null)
            {
                d.Add("Title", XmlValue(doc, "title"));
                d.Add("Author", XmlValue(doc, "creator"));
                d.Add("Last modified by", XmlValue(doc, "lastModifiedBy"));
                string? created = XmlValue(doc, "created");
                if (created is not null) d.Add("Created", NormalizeDate(created));
                string? modified = XmlValue(doc, "modified");
                if (modified is not null) d.Add("Modified", NormalizeDate(modified));
            }
        }

        string? app = ReadZipText(zip, "docProps/app.xml", 64 * 1024);
        XDocument? appDoc = app is null ? null : TryParseXml(app);
        if (appDoc is not null)
        {
            if (ext.StartsWith(".doc", StringComparison.Ordinal))
            {
                d.Add("Pages", XmlValue(appDoc, "Pages"));
                d.Add("Words", XmlValue(appDoc, "Words"));
                d.Add("Application", XmlValue(appDoc, "Application"));
            }
            else if (ext.StartsWith(".ppt", StringComparison.Ordinal))
            {
                d.Add("Slides", XmlValue(appDoc, "Slides"));
                d.Add("Application", XmlValue(appDoc, "Application"));
            }
            else
            {
                d.Add("Application", XmlValue(appDoc, "Application"));
            }
        }

        if (ext.StartsWith(".xls", StringComparison.Ordinal))
        {
            int sheets = zip.Entries.Count(e =>
                e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) &&
                e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
            if (sheets > 0) d.Add("Worksheets", sheets.ToString(CultureInfo.InvariantCulture));
        }

        if (ext.StartsWith(".ppt", StringComparison.Ordinal))
        {
            int slides = zip.Entries.Count(e =>
                e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) &&
                e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
            if (slides > 0) d.Add("Slide files", slides.ToString(CultureInfo.InvariantCulture));
        }

        int parts = zip.Entries.Count;
        d.Add("Package parts", parts.ToString(CultureInfo.InvariantCulture));
    }

    private static string OfficeName(string ext) => ext switch
    {
        ".docx" or ".docm" or ".doc" => "Word document (OOXML)",
        ".xlsx" or ".xlsm" or ".xls" => "Excel workbook (OOXML)",
        ".pptx" or ".pptm" or ".ppt" => "PowerPoint presentation (OOXML)",
        _ => "Office document"
    };

    private static string? XmlValue(XDocument doc, string localName)
    {
        XElement? element = doc.Descendants().FirstOrDefault(e =>
            string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));
        string? value = element?.Value?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static XDocument? TryParseXml(string text)
    {
        try
        {
            return XDocument.Parse(text, LoadOptions.None);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or ArgumentException)
        {
            return null;
        }
    }

    private static string NormalizeDate(string value)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
        {
            return parsed.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
        }
        return value;
    }

    private static string? ReadZipText(ZipArchive zip, string name, int maxChars)
    {
        try
        {
            ZipArchiveEntry? entry = zip.GetEntry(name);
            if (entry is null) return null;
            using Stream stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            var buffer = new char[Math.Min(maxChars, 65536)];
            int read = reader.Read(buffer, 0, buffer.Length);
            return read <= 0 ? null : new string(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- archive

    private static void ParseArchive(byte[] data, Draft d, string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();

        if (IsZip(data))
        {
            using var zip = TryOpenZip(data);
            if (zip is null)
            {
                d.Add("Format", "ZIP");
                d.Notes.Add("Zip directory could not be read (truncated or encrypted)");
                return;
            }

            d.Add("Format", ext is ".jar" or ".war" ? "ZIP (Java archive)" : "ZIP");
            int count = zip.Entries.Count;
            d.Add("Entries", count.ToString(CultureInfo.InvariantCulture));

            int shown = 0;
            var preview = new StringBuilder();
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (shown >= 8) break;
                if (preview.Length > 0) preview.Append('\n');
                preview.Append(entry.FullName);
                if (entry.Length > 0)
                {
                    preview.Append("  (").Append(FormatBytes(entry.Length)).Append(')');
                }
                shown++;
            }

            if (preview.Length > 0) d.Add("First entries", preview.ToString());
            if (count > shown) d.Notes.Add(count.ToString(CultureInfo.InvariantCulture) + " entries total");
            return;
        }

        if (StartsWith(data, new byte[] { 0x1F, 0x8B }))
        {
            d.Add("Format", "GZip");
            if (data.Length > 10)
            {
                uint stamp = ReadU32LE(data, 4);
                if (stamp > 0 && stamp < 4102444800)
                {
                    DateTime when = DateTimeOffset.FromUnixTimeSeconds(stamp).UtcDateTime;
                    d.Add("Modified", when.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC");
                }
                string? name = ReadGzipName(data);
                if (name is not null) d.Add("Original name", name);
            }
            d.Notes.Add("Compressed stream; contents not enumerated");
            return;
        }

        if (StartsWith(data, "BZh"u8))
        {
            d.Add("Format", "BZip2");
            d.Notes.Add("Compressed stream; contents not enumerated");
            return;
        }

        if (StartsWith(data, new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 }))
        {
            d.Add("Format", "XZ");
            d.Notes.Add("Compressed stream; contents not enumerated");
            return;
        }

        if (StartsWith(data, new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }))
        {
            d.Add("Format", "7-Zip");
            d.Notes.Add("Archive directory not parsed");
            return;
        }

        if (StartsWith(data, "Rar!"u8))
        {
            d.Add("Format", "RAR");
            d.Notes.Add("Archive directory not parsed");
            return;
        }

        if (ext is ".tar" or ".tgz" || LooksLikeTar(data))
        {
            ParseTar(data, d);
            return;
        }

        d.Add("Format", "Archive (" + ext.TrimStart('.') + ")");
        d.Notes.Add("Container not recognised");
    }

    private static string? ReadGzipName(byte[] data)
    {
        if (data.Length < 10) return null;
        byte flags = data[3];
        if ((flags & 0x08) == 0) return null;
        int pos = 10;
        if ((flags & 0x04) != 0)
        {
            if (pos + 2 > data.Length) return null;
            pos += 2 + ReadU16LE(data, pos);
        }
        if (pos >= data.Length) return null;
        int end = pos;
        while (end < data.Length && data[end] != 0 && end - pos < 512) end++;
        return end == pos ? null : Encoding.UTF8.GetString(data, pos, end - pos);
    }

    private static bool LooksLikeTar(byte[] data)
    {
        if (data.Length < 512) return false;
        return data.AsSpan(257, 5).SequenceEqual("ustar"u8);
    }

    private static void ParseTar(byte[] data, Draft d)
    {
        d.Add("Format", "TAR");
        int count = 0;
        int guard = 0;
        long pos = 0;
        var preview = new StringBuilder();
        int zeros = 0;

        while (pos + 512 <= data.Length && guard++ < 20000)
        {
            int header = (int)pos;
            bool empty = true;
            for (int i = 0; i < 512; i++)
            {
                if (data[header + i] != 0)
                {
                    empty = false;
                    break;
                }
            }
            if (empty)
            {
                zeros++;
                if (zeros >= 2) break;
                pos += 512;
                continue;
            }
            zeros = 0;

            string name = ReadNullTerminated(data, header, 100);
            string prefix = ReadNullTerminated(data, header + 345, 155);
            if (prefix.Length > 0) name = prefix + "/" + name;

            long size = ReadOctal(data, header + 124, 12);

            if (name.Length > 0 && !name.StartsWith("./", StringComparison.Ordinal))
            {
                count++;
                if (count <= 8)
                {
                    if (preview.Length > 0) preview.Append('\n');
                    preview.Append(name);
                    if (size > 0) preview.Append("  (").Append(FormatBytes(size)).Append(')');
                }
            }

            long skip = 512 + ((size + 511) / 512) * 512;
            if (skip <= 0 || pos + skip > data.Length) break;
            pos += skip;
        }

        if (count == 0)
        {
            d.Notes.Add("No tar headers found");
            return;
        }

        d.Add("Entries", count.ToString(CultureInfo.InvariantCulture));
        if (preview.Length > 0) d.Add("First entries", preview.ToString());
        if (count > 8) d.Notes.Add("Listing truncated to the first 8 entries");
    }

    private static string ReadNullTerminated(byte[] data, int offset, int max)
    {
        int end = offset;
        int limit = Math.Min(data.Length, offset + max);
        while (end < limit && data[end] != 0) end++;
        return end == offset ? string.Empty : Encoding.UTF8.GetString(data, offset, end - offset).Trim();
    }

    private static long ReadOctal(byte[] data, int offset, int length)
    {
        long value = 0;
        int limit = Math.Min(data.Length, offset + length);
        for (int i = offset; i < limit; i++)
        {
            byte b = data[i];
            if (b is 32 or 0) continue;
            if (b is < (byte)'0' or > (byte)'7') break;
            value = (value << 3) + (b - '0');
        }
        return value;
    }

    private static bool IsZip(byte[] data) =>
        data.Length >= 4 && data[0] == 0x50 && data[1] == 0x4B &&
        (data[2] == 0x03 || data[2] == 0x05 || data[2] == 0x07);

    private static ZipArchive? TryOpenZip(byte[] data)
    {
        try
        {
            return new ZipArchive(new MemoryStream(data, writable: false), ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- binary

    private static void ParseBinary(byte[] data, Draft d)
    {
        d.Add("Format", "Binary");
        ParseBinaryHead(data, d);
    }

    private static void ParseBinaryHead(byte[] data, Draft d)
    {
        int shown = Math.Min(16, data.Length);
        var hex = new StringBuilder(shown * 3);
        for (int i = 0; i < shown; i++)
        {
            if (i > 0) hex.Append(' ');
            hex.Append(data[i].ToString("X2", CultureInfo.InvariantCulture));
        }

        if (shown > 0) d.Add("Magic", hex.ToString());
        string? guess = GuessMagic(data);
        if (guess is not null) d.Add("Looks like", guess);
    }

    private static string? GuessMagic(byte[] data)
    {
        if (StartsWith(data, "%PDF"u8)) return "PDF document";
        if (StartsWith(data, new byte[] { 0x89, 0x50, 0x4E, 0x47 })) return "PNG image";
        if (StartsWith(data, new byte[] { 0xFF, 0xD8, 0xFF })) return "JPEG image";
        if (StartsWith(data, "GIF8"u8)) return "GIF image";
        if (StartsWith(data, "PK"u8) && data.Length > 3 && data[2] is 0x03 or 0x05 or 0x07) return "ZIP container";
        if (StartsWith(data, new byte[] { 0x1F, 0x8B })) return "GZip stream";
        if (StartsWith(data, "BZh"u8)) return "BZip2 stream";
        if (StartsWith(data, new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A })) return "XZ stream";
        if (StartsWith(data, new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C })) return "7-Zip archive";
        if (StartsWith(data, "Rar!"u8)) return "RAR archive";
        if (StartsWith(data, new byte[] { 0x7F, 0x45, 0x4C, 0x46 })) return "ELF executable";
        if (StartsWith(data, new byte[] { 0x4D, 0x5A })) return "Windows PE executable";
        if (StartsWith(data, new byte[] { 0xCF, 0xFA, 0xED, 0xFE }) ||
            StartsWith(data, new byte[] { 0xCE, 0xFA, 0xED, 0xFE }) ||
            StartsWith(data, new byte[] { 0xCA, 0xFE, 0xBA, 0xBE })) return "Mach-O executable";
        if (StartsWith(data, "SQLite format 3"u8)) return "SQLite database";
        if (StartsWith(data, new byte[] { 0xD0, 0xCF, 0x11, 0xE0 })) return "OLE2 compound document";
        if (StartsWith(data, "ID3"u8)) return "MP3 audio with ID3 tag";
        if (StartsWith(data, "fLaC"u8)) return "FLAC audio";
        if (StartsWith(data, "OggS"u8)) return "Ogg stream";
        if (StartsWith(data, "RIFF"u8) && data.Length >= 12)
        {
            if (data.AsSpan(8, 4).SequenceEqual("WAVE"u8)) return "WAV audio";
            if (data.AsSpan(8, 4).SequenceEqual("AVI "u8)) return "AVI video";
            if (data.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return "WebP image";
        }
        if (data.Length >= 12 && data.AsSpan(4, 4).SequenceEqual("ftyp"u8)) return "MP4 container";
        if (StartsWith(data, new byte[] { 0x1A, 0x45, 0xDF, 0xA3 })) return "Matroska/WebM";
        if (StartsWith(data, new byte[] { 0x00, 0x01, 0x00, 0x00 }) || StartsWith(data, "OTTO"u8) ||
            StartsWith(data, "ttcf"u8)) return "OpenType/TrueType font";
        if (StartsWith(data, "wOFF"u8) || StartsWith(data, "wOF2"u8)) return "WOFF web font";
        if (StartsWith(data, "{\\rtf"u8)) return "RTF document";
        return null;
    }

    // ---------------------------------------------------------------- small helpers

    private static string DescribeKind(InspectionKind kind) => kind switch
    {
        InspectionKind.Pdf => "PDF",
        InspectionKind.Audio => "Audio",
        InspectionKind.Video => "Video",
        InspectionKind.Font => "Font",
        InspectionKind.Markdown => "Markdown",
        InspectionKind.Archive => "Archive",
        InspectionKind.Office => "Office document",
        InspectionKind.Image => "Image",
        InspectionKind.Text => "Text",
        _ => "Binary"
    };

    private static bool StartsWith(byte[] data, ReadOnlySpan<byte> prefix) =>
        data.AsSpan().StartsWith(prefix);

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "unknown";
        if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";

        double value = bytes;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return value.ToString("0.##", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    private static string FormatDuration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "unknown";

        long total = (long)Math.Round(seconds, MidpointRounding.AwayFromZero);
        long hours = total / 3600;
        long minutes = total % 3600 / 60;
        long secs = total % 60;

        return hours > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", hours, minutes, secs)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", minutes, secs);
    }

    private static ushort ReadU16BE(byte[] data, int offset) =>
        offset < 0 || offset + 2 > data.Length ? (ushort)0
            : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));

    private static ushort ReadU16LE(byte[] data, int offset) =>
        offset < 0 || offset + 2 > data.Length ? (ushort)0
            : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));

    private static uint ReadU32BE(byte[] data, int offset) =>
        offset < 0 || offset + 4 > data.Length ? 0
            : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));

    private static uint ReadU32LE(byte[] data, int offset) =>
        offset < 0 || offset + 4 > data.Length ? 0
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

    private static int ReadI32LE(byte[] data, int offset) =>
        offset < 0 || offset + 4 > data.Length ? 0
            : BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));

    private static double ReadExtended80(byte[] data, int offset)
    {
        if (offset < 0 || offset + 10 > data.Length) return 0;
        int exponent = (data[offset] << 8) | data[offset + 1];
        bool negative = (exponent & 0x8000) != 0;
        exponent &= 0x7FFF;

        ulong mantissa = 0;
        for (int i = 2; i < 10; i++) mantissa = (mantissa << 8) | data[offset + i];

        if (exponent == 0 && mantissa == 0) return 0;
        if (exponent == 0x7FFF) return 0;

        double value = mantissa * Math.Pow(2, exponent - 16383 - 63);
        return negative ? -value : value;
    }
}
