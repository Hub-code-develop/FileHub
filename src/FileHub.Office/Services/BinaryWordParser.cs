using System.Text;
using FileHub.Office.Models;

namespace FileHub.Office.Services;

/// <summary>
/// Best-effort reader for the Word 97 binary container used by Kingsoft WPS Writer (.wps/.wpt).
/// It follows the FIB -> CLX -> piece table path to recover paragraph text. Character and
/// paragraph formatting, tables, images and fields are not reconstructed.
/// </summary>
public static class BinaryWordParser
{
    private const int FibFcClx97 = 0x01A2;
    private const int FibLcbClx97 = 0x01A6;

    static BinaryWordParser()
    {
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { /* optional */ }
    }

    public static List<WordBlock> Parse(IReadOnlyDictionary<string, byte[]> streams, out string warning)
    {
        warning = string.Empty;
        var blocks = new List<WordBlock>();
        if (!streams.TryGetValue("WordDocument", out var word) || word.Length < 0x200)
        {
            warning = "WordDocument stream is missing or too small.";
            return blocks;
        }

        try
        {
            var flags = ReadU16(word, 0x0A);
            var tableName = (flags & 0x0200) != 0 ? "1Table" : "0Table";
            if (!streams.TryGetValue(tableName, out var table))
            {
                table = streams.TryGetValue("0Table", out var zero) ? zero
                    : streams.TryGetValue("1Table", out var one) ? one : Array.Empty<byte>();
            }

            var fcClx = ReadI32(word, FibFcClx97);
            var lcbClx = ReadI32(word, FibLcbClx97);
            var text = string.Empty;
            if (fcClx >= 0 && lcbClx > 0 && fcClx + lcbClx <= table.Length)
                text = ReadPieceTable(table, fcClx, lcbClx, word);
            if (text.Length == 0)
            {
                text = FallbackText(word);
                if (text.Length > 0) warning = "Piece table unavailable; text recovered heuristically.";
            }

            foreach (var paragraph in SplitParagraphs(text))
                blocks.Add(new WordParagraph { Runs = { new WordRun { Text = paragraph } } });
        }
        catch (Exception ex)
        {
            warning = "WPS Writer decode failed: " + ex.Message;
        }
        return blocks;
    }

    private static string ReadPieceTable(byte[] table, int offset, int length, byte[] wordStream)
    {
        var end = Math.Min(table.Length, offset + Math.Max(length, 5 + length));
        var position = offset;
        while (position < end)
        {
            var marker = table[position];
            if (marker == 0x01)
            {
                if (position + 3 > end) break;
                var size = ReadU16(table, position + 1);
                position += 3 + size;
                continue;
            }
            if (marker != 0x02) break;
            if (position + 5 > end) break;
            var lcb = ReadI32(table, position + 1);
            var plcStart = position + 5;
            if (lcb <= 4 || plcStart + lcb > end) break;
            return ReadPlcPcd(table, plcStart, lcb, wordStream);
        }
        return string.Empty;
    }

    private static string ReadPlcPcd(byte[] table, int offset, int length, byte[] wordStream)
    {
        var pieces = (length - 4) / 12;
        if (pieces <= 0) return string.Empty;
        var builder = new StringBuilder();
        for (var i = 0; i < pieces; i++)
        {
            var cpStart = ReadI32(table, offset + i * 4);
            var cpEnd = ReadI32(table, offset + (i + 1) * 4);
            var chars = cpEnd - cpStart;
            if (chars <= 0 || chars > 40_000_000) continue;

            var pcd = offset + (pieces + 1) * 4 + i * 8;
            var fcRaw = ReadU32(table, pcd + 2);
            var compressed = (fcRaw & 0x40000000) != 0;
            var fc = (int)(fcRaw & 0x3FFFFFFF);
            if (compressed) fc /= 2;
            if (fc < 0) continue;

            var bytes = compressed ? chars : chars * 2;
            if (fc + bytes > wordStream.Length) bytes = Math.Max(0, wordStream.Length - fc);
            if (bytes <= 0) continue;
            var encoding = compressed ? GetAnsiEncoding() : Encoding.Unicode;
            builder.Append(encoding.GetString(wordStream, fc, bytes));
        }
        return builder.ToString();
    }

    private static string FallbackText(byte[] word)
    {
        var builder = new StringBuilder();
        for (var i = 0x200; i + 1 < word.Length; i += 2)
        {
            var value = (char)ReadU16(word, i);
            if (value is '\r' or '\n' or '\t' || (value >= 0x20 && value < 0xE000 && value != 0xFFFD))
                builder.Append(value);
            else if (builder.Length > 0 && builder[^1] != ' ')
                builder.Append(' ');
        }
        return builder.ToString();
    }

    private static IEnumerable<string> SplitParagraphs(string text)
    {
        var cleaned = text
            .Replace("\x07", " | ", StringComparison.Ordinal)
            .Replace("\x0B", "\n", StringComparison.Ordinal)
            .Replace("\x0C", "\n", StringComparison.Ordinal)
            .Replace("\x13", string.Empty, StringComparison.Ordinal)
            .Replace("\x14", string.Empty, StringComparison.Ordinal)
            .Replace("\x15", string.Empty, StringComparison.Ordinal);
        foreach (var line in cleaned.Split('\r'))
        {
            var value = line.Replace('\n', ' ').Trim();
            if (value.Length == 0) continue;
            // Drop binary noise that survives the fallback path.
            if (value.Count(c => char.IsControl(c)) > value.Length / 3) continue;
            yield return value;
        }
    }

    private static Encoding GetAnsiEncoding()
    {
        foreach (var codePage in new[] { 936, 950, 1252 })
        {
            try { return Encoding.GetEncoding(codePage); } catch { /* try next */ }
        }
        return Encoding.Latin1;
    }

    private static int ReadU16(byte[] data, int offset) =>
        offset >= 0 && offset + 1 < data.Length ? data[offset] | data[offset + 1] << 8 : 0;

    private static uint ReadU32(byte[] data, int offset) =>
        offset >= 0 && offset + 3 < data.Length
            ? (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24)
            : 0;

    private static int ReadI32(byte[] data, int offset) => unchecked((int)ReadU32(data, offset));
}
