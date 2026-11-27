using System.Text;
using FileHub.Office.Models;

namespace FileHub.Office.Services;

/// <summary>
/// Decodes Kingsoft WPS native documents (.wps/.wpt Writer, .et/.ett Spreadsheet, .dps/.dpt
/// Presentation) into FileHub's editable OOXML model. Three containers are recognised:
/// OOXML packages renamed to a WPS extension, legacy OLE/BIFF/WPS binaries, and RTF-based Writer
/// documents. Binary decoding is best-effort and focuses on text, cells and slide text.
/// </summary>
public static class WpsFormatService
{
    private static readonly HashSet<string> WriterExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".wps", ".wpt" };
    private static readonly HashSet<string> SpreadsheetExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".et", ".ett" };
    private static readonly HashSet<string> PresentationExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".dps", ".dpt" };

    public static bool IsWpsFile(string path) => KindOf(path) != WpsDocumentKind.Unknown;

    public static WpsDocumentKind KindOf(string path)
    {
        var extension = Path.GetExtension(path);
        if (WriterExtensions.Contains(extension)) return WpsDocumentKind.Writer;
        if (SpreadsheetExtensions.Contains(extension)) return WpsDocumentKind.Spreadsheet;
        if (PresentationExtensions.Contains(extension)) return WpsDocumentKind.Presentation;
        return WpsDocumentKind.Unknown;
    }

    /// <summary>Loads a WPS document through a temporary OOXML conversion.</summary>
    public static OfficeDocument Load(string path)
    {
        var kind = KindOf(path);
        if (kind == WpsDocumentKind.Unknown)
            throw new NotSupportedException($"Unsupported WPS document: {path}");
        if (!File.Exists(path))
            throw new FileNotFoundException("WPS document was not found.", path);

        var bytes = File.ReadAllBytes(path);
        OfficeDocument document;
        string sourceFormat;

        if (IsZip(bytes))
        {
            var temp = CreateTempPath(kind);
            File.WriteAllBytes(temp, bytes);
            document = OfficeDocumentService.Load(temp);
            sourceFormat = "WPS " + FamilyName(kind) + " (OOXML container)";
        }
        else if (IsCompoundFile(bytes))
        {
            if (!CompoundFileReader.TryRead(bytes, out var streams))
                throw new NotSupportedException("The WPS compound document could not be parsed.");
            if (streams.ContainsKey("EncryptedPackage") || streams.ContainsKey("EncryptionInfo"))
                throw new NotSupportedException("Encrypted WPS documents are not supported.");

            document = kind switch
            {
                WpsDocumentKind.Writer => DecodeWriter(streams, path),
                WpsDocumentKind.Spreadsheet => DecodeSpreadsheet(streams, path),
                WpsDocumentKind.Presentation => DecodePresentation(streams, path),
                _ => throw new NotSupportedException("Unknown WPS document family.")
            };
            sourceFormat = "WPS " + FamilyName(kind) + " (binary)";
        }
        else if (IsRtf(bytes))
        {
            document = DecodeRtf(bytes, path);
            sourceFormat = "WPS Writer (RTF)";
        }
        else
        {
            throw new NotSupportedException(
                "Unrecognised WPS container. Only OOXML, OLE binary and RTF-based WPS files are supported.");
        }

        document.SourcePath = path;
        document.SourceFormat = sourceFormat;
        return document;
    }

    private static OfficeDocument DecodeWriter(IReadOnlyDictionary<string, byte[]> streams, string sourcePath)
    {
        var blocks = BinaryWordParser.Parse(streams, out var warning);
        if (blocks.Count == 0)
            throw new NotSupportedException(string.IsNullOrEmpty(warning)
                ? "No readable text was found in this WPS Writer document."
                : warning);

        var path = CreateTempPath(WpsDocumentKind.Writer);
        OfficeDocumentService.CreateNew(path, OfficeDocumentKind.Word);
        var document = OfficeDocumentService.Load(path);
        document.Blocks.Clear();
        foreach (var block in blocks) document.Blocks.Add(block);
        if (document.Blocks.Count == 0) document.Blocks.Add(new WordParagraph());
        OfficeDocumentService.Save(document);
        var decoded = OfficeDocumentService.Load(path);
        decoded.SourcePath = sourcePath;
        decoded.SourceFormat = "WPS Writer (binary)";
        return decoded;
    }

    private static OfficeDocument DecodeSpreadsheet(IReadOnlyDictionary<string, byte[]> streams, string sourcePath)
    {
        var sheets = BinaryExcelParser.Parse(streams, out var warning);
        if (sheets.Count == 0 || sheets.All(s => s.Cells.Count == 0))
            throw new NotSupportedException(string.IsNullOrEmpty(warning)
                ? "No readable cells were found in this WPS Spreadsheet document."
                : warning);

        var path = CreateTempPath(WpsDocumentKind.Spreadsheet);
        OfficeDocumentService.CreateNew(path, OfficeDocumentKind.Excel);
        var document = OfficeDocumentService.Load(path);
        document.Sheets.Clear();
        foreach (var sheet in sheets) document.Sheets.Add(sheet);
        OfficeDocumentService.Save(document);
        var decoded = OfficeDocumentService.Load(path);
        decoded.SourcePath = sourcePath;
        decoded.SourceFormat = "WPS Spreadsheet (binary)";
        return decoded;
    }

    private static OfficeDocument DecodePresentation(IReadOnlyDictionary<string, byte[]> streams, string sourcePath)
    {
        var slides = BinaryPresentationParser.Parse(streams, out var warning);
        if (slides.Count == 0)
            throw new NotSupportedException(string.IsNullOrEmpty(warning)
                ? "No readable slide text was found in this WPS Presentation document."
                : warning);

        var path = CreateTempPath(WpsDocumentKind.Presentation);
        OfficeDocumentService.CreateNew(path, OfficeDocumentKind.PowerPoint);
        var document = OfficeDocumentService.Load(path);
        document.Slides.Clear();
        foreach (var slide in slides) document.Slides.Add(slide);
        OfficeDocumentService.Save(document);
        var decoded = OfficeDocumentService.Load(path);
        decoded.SourcePath = sourcePath;
        decoded.SourceFormat = "WPS Presentation (binary)";
        return decoded;
    }

    private static OfficeDocument DecodeRtf(byte[] bytes, string sourcePath)
    {
        var paragraphs = RtfTextParser.Parse(Encoding.Latin1.GetString(bytes));
        var path = CreateTempPath(WpsDocumentKind.Writer);
        OfficeDocumentService.CreateNew(path, OfficeDocumentKind.Word);
        var document = OfficeDocumentService.Load(path);
        document.Blocks.Clear();
        foreach (var paragraph in paragraphs)
            document.Blocks.Add(new WordParagraph { Runs = { new WordRun { Text = paragraph } } });
        if (document.Blocks.Count == 0) document.Blocks.Add(new WordParagraph());
        OfficeDocumentService.Save(document);
        var decoded = OfficeDocumentService.Load(path);
        decoded.SourcePath = sourcePath;
        decoded.SourceFormat = "WPS Writer (RTF)";
        return decoded;
    }

    /// <summary>Converts a WPS document into a sibling OOXML file chosen by the user.</summary>
    public static void Export(string sourcePath, string targetPath)
    {
        var document = Load(sourcePath);
        var kind = Path.GetExtension(targetPath).ToLowerInvariant() switch
        {
            ".docx" or ".docm" => OfficeDocumentKind.Word,
            ".xlsx" or ".xlsm" => OfficeDocumentKind.Excel,
            ".pptx" or ".pptm" => OfficeDocumentKind.PowerPoint,
            _ => document.Kind
        };
        OfficeDocumentService.CreateNew(targetPath, kind);
        var target = OfficeDocumentService.Load(targetPath);
        target.Blocks.Clear();
        target.Sheets.Clear();
        target.Slides.Clear();
        foreach (var block in document.Blocks) target.Blocks.Add(block);
        foreach (var sheet in document.Sheets) target.Sheets.Add(sheet);
        foreach (var slide in document.Slides) target.Slides.Add(slide);
        OfficeDocumentService.Save(target);
    }

    public static string SuggestedFileName(string sourcePath)
    {
        var extension = KindOf(sourcePath) switch
        {
            WpsDocumentKind.Writer => ".docx",
            WpsDocumentKind.Spreadsheet => ".xlsx",
            WpsDocumentKind.Presentation => ".pptx",
            _ => ".docx"
        };
        return Path.GetFileNameWithoutExtension(sourcePath) + extension;
    }

    private static string CreateTempPath(WpsDocumentKind kind)
    {
        var directory = Path.Combine(Path.GetTempPath(), "filehub-wps", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var extension = kind switch
        {
            WpsDocumentKind.Writer => ".docx",
            WpsDocumentKind.Spreadsheet => ".xlsx",
            WpsDocumentKind.Presentation => ".pptx",
            _ => ".docx"
        };
        return Path.Combine(directory, "decoded" + extension);
    }

    private static string FamilyName(WpsDocumentKind kind) => kind switch
    {
        WpsDocumentKind.Writer => "Writer",
        WpsDocumentKind.Spreadsheet => "Spreadsheet",
        WpsDocumentKind.Presentation => "Presentation",
        _ => "Document"
    };

    private static bool IsZip(byte[] bytes) =>
        bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04;

    private static bool IsCompoundFile(byte[] bytes) =>
        bytes.Length >= 8 && bytes[0] == 0xD0 && bytes[1] == 0xCF && bytes[2] == 0x11 && bytes[3] == 0xE0 &&
        bytes[4] == 0xA1 && bytes[5] == 0xB1 && bytes[6] == 0x1A && bytes[7] == 0xE1;

    private static bool IsRtf(byte[] bytes)
    {
        var prefix = Encoding.ASCII.GetString(bytes, 0, Math.Min(5, bytes.Length));
        return prefix.StartsWith("{\\rtf", StringComparison.Ordinal);
    }
}

/// <summary>Very small RTF text extractor used for RTF-flavoured .wps documents.</summary>
internal static class RtfTextParser
{
    public static List<string> Parse(string rtf)
    {
        var paragraphs = new List<string>();
        var builder = new StringBuilder();
        var i = 0;
        var depth = 0;
        var skipDepth = -1;
        while (i < rtf.Length)
        {
            var c = rtf[i];
            if (c == '{')
            {
                depth++;
                i++;
                if (IsSkippedGroup(rtf, i)) skipDepth = depth;
                continue;
            }
            if (c == '}')
            {
                if (skipDepth == depth) skipDepth = -1;
                depth--;
                i++;
                continue;
            }
            if (skipDepth >= 0)
            {
                i++;
                continue;
            }
            if (c == '\\')
            {
                i++;
                if (i >= rtf.Length) break;
                var next = rtf[i];
                if (next is '\\' or '{' or '}')
                {
                    builder.Append(next);
                    i++;
                    continue;
                }
                if (next == '\'')
                {
                    if (i + 2 < rtf.Length &&
                        int.TryParse(rtf.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out var code))
                        builder.Append((char)code);
                    i += 3;
                    continue;
                }
                if (next == 'u')
                {
                    i++;
                    var start = i;
                    if (i < rtf.Length && rtf[i] == '-') i++;
                    while (i < rtf.Length && char.IsDigit(rtf[i])) i++;
                    if (int.TryParse(rtf.AsSpan(start, i - start), out var unicode))
                        builder.Append((char)(unicode < 0 ? unicode + 65536 : unicode));
                    if (i < rtf.Length && rtf[i] == '?') i++;
                    continue;
                }
                var wordStart = i;
                while (i < rtf.Length && char.IsLetter(rtf[i])) i++;
                var word = rtf[wordStart..i];
                while (i < rtf.Length && (char.IsDigit(rtf[i]) || rtf[i] == '-')) i++;
                if (i < rtf.Length && rtf[i] == ' ') i++;
                if (word is "par" or "line" or "sect")
                {
                    Flush(paragraphs, builder);
                }
                else if (word == "tab")
                {
                    builder.Append('\t');
                }
                continue;
            }
            if (c == '\r' || c == '\n')
            {
                Flush(paragraphs, builder);
                i++;
                continue;
            }
            builder.Append(c);
            i++;
        }
        Flush(paragraphs, builder);
        return paragraphs;
    }

    private static bool IsSkippedGroup(string rtf, int index)
    {
        var word = new StringBuilder();
        var i = index;
        if (i < rtf.Length && rtf[i] == '\\') i++;
        if (i < rtf.Length && rtf[i] == '*') i++;
        for (; i < rtf.Length && word.Length < 12; i++)
        {
            if (char.IsLetter(rtf[i])) word.Append(rtf[i]);
            else break;
        }
        return word.ToString() is "fonttbl" or "colortbl" or "stylesheet" or "info"
            or "pict" or "object" or "header" or "footer" or "generator";
    }

    private static void Flush(List<string> paragraphs, StringBuilder builder)
    {
        var text = builder.ToString().Trim();
        if (text.Length > 0) paragraphs.Add(text);
        builder.Clear();
    }
}
