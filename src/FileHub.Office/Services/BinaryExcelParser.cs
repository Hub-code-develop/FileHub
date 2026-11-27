using System.Globalization;
using System.Text;
using FileHub.Office.Models;

namespace FileHub.Office.Services;

/// <summary>
/// Minimal BIFF8 reader used for Kingsoft WPS Spreadsheet (.et/.ett) and legacy .xls files.
/// It understands sheet boundaries, the shared string table and the common cell records.
/// Formulas are returned with their cached value; styles and charts are not reconstructed.
/// </summary>
public static class BinaryExcelParser
{
    private const ushort Bof = 0x0809;
    private const ushort Eof = 0x000A;
    private const ushort BoundSheet = 0x0085;
    private const ushort Sst = 0x00FC;
    private const ushort Continue = 0x003C;
    private const ushort LabelSst = 0x00FD;
    private const ushort Label = 0x0204;
    private const ushort Number = 0x0203;
    private const ushort Rk = 0x027E;
    private const ushort MulRk = 0x00BD;
    private const ushort Formula = 0x0006;
    private const ushort StringRecord = 0x0207;
    private const ushort BoolErr = 0x0205;

    static BinaryExcelParser()
    {
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { /* optional */ }
    }

    public static List<ExcelSheet> Parse(IReadOnlyDictionary<string, byte[]> streams, out string warning)
    {
        warning = string.Empty;
        var result = new List<ExcelSheet>();
        if (!streams.TryGetValue("Workbook", out var workbook) &&
            !streams.TryGetValue("Book", out workbook))
        {
            warning = "Workbook stream is missing.";
            return result;
        }

        try
        {
            var sheets = new List<(string Name, int Offset)>();
            var strings = new List<string>();
            var reader = new BiffReader(workbook);
            while (reader.TryRead(out var id, out var offset, out var length))
            {
                if (id == BoundSheet && length >= 8)
                    sheets.Add((ReadSheetName(workbook, offset, length), (int)ReadU32(workbook, offset)));
                else if (id == Sst)
                {
                    var chunks = new List<byte[]> { Slice(workbook, offset, length) };
                    while (reader.Peek(out var nextId, out _, out var nextLength) && nextId == Continue)
                    {
                        reader.TryRead(out _, out var nextOffset, out _);
                        chunks.Add(Slice(workbook, nextOffset, nextLength));
                    }
                    strings.AddRange(ReadSst(chunks));
                }
            }

            if (sheets.Count == 0)
            {
                var firstSheet = FindFirstWorksheet(workbook);
                sheets.Add(("Sheet1", firstSheet));
            }

            foreach (var (name, offset) in sheets)
            {
                var sheet = new ExcelSheet { Name = string.IsNullOrWhiteSpace(name) ? "Sheet" : name };
                ReadWorksheet(workbook, offset, sheet, strings);
                FillWidths(sheet);
                result.Add(sheet);
            }
        }
        catch (Exception ex)
        {
            warning = "WPS Spreadsheet decode failed: " + ex.Message;
        }

        if (result.Count == 0) result.Add(new ExcelSheet { Name = "Sheet1" });
        return result;
    }

    private static void ReadWorksheet(byte[] data, int start, ExcelSheet sheet, IReadOnlyList<string> strings)
    {
        if (start <= 0 || start >= data.Length) return;
        var reader = new BiffReader(data, start);
        ExcelCell? pendingFormula = null;
        var maxRow = 0;
        var maxColumn = 0;

        while (reader.TryRead(out var id, out var offset, out var length))
        {
            if (id == Eof) break;
            if (length < 0 || offset + length > data.Length) break;
            switch (id)
            {
                case LabelSst when length >= 10:
                {
                    var row = ReadU16(data, offset);
                    var column = ReadU16(data, offset + 2);
                    var index = (int)ReadU32(data, offset + 6);
                    var text = index >= 0 && index < strings.Count ? strings[index] : string.Empty;
                    Set(sheet, row, column, text);
                    Track(ref maxRow, ref maxColumn, row, column);
                    break;
                }
                case Label when length >= 9:
                {
                    var row = ReadU16(data, offset);
                    var column = ReadU16(data, offset + 2);
                    var text = ReadInlineString(data, offset + 6, length - 6);
                    Set(sheet, row, column, text);
                    Track(ref maxRow, ref maxColumn, row, column);
                    break;
                }
                case Number when length >= 14:
                {
                    var row = ReadU16(data, offset);
                    var column = ReadU16(data, offset + 2);
                    var value = BitConverter.Int64BitsToDouble((long)ReadU64(data, offset + 6));
                    Set(sheet, row, column, value.ToString(CultureInfo.InvariantCulture));
                    Track(ref maxRow, ref maxColumn, row, column);
                    break;
                }
                case Rk when length >= 10:
                {
                    var row = ReadU16(data, offset);
                    var column = ReadU16(data, offset + 2);
                    Set(sheet, row, column, DecodeRk(ReadU32(data, offset + 6)).ToString(CultureInfo.InvariantCulture));
                    Track(ref maxRow, ref maxColumn, row, column);
                    break;
                }
                case MulRk when length >= 12:
                {
                    var row = ReadU16(data, offset);
                    var first = ReadU16(data, offset + 2);
                    var count = (length - 6) / 6;
                    for (var i = 0; i < count; i++)
                    {
                        var column = first + i;
                        var value = DecodeRk(ReadU32(data, offset + 4 + i * 6 + 2));
                        Set(sheet, row, column, value.ToString(CultureInfo.InvariantCulture));
                        Track(ref maxRow, ref maxColumn, row, column);
                    }
                    break;
                }
                case Formula when length >= 14:
                {
                    var row = ReadU16(data, offset);
                    var column = ReadU16(data, offset + 2);
                    var cell = Set(sheet, row, column, string.Empty);
                    var result = offset + 6;
                    if (data[result + 6] == 0xFF && data[result + 7] == 0xFF)
                    {
                        switch (data[result])
                        {
                            case 0:
                                pendingFormula = cell;
                                break;
                            case 1:
                                cell.Display = data[result + 2] != 0 ? "TRUE" : "FALSE";
                                break;
                            case 2:
                                cell.Display = "#ERROR";
                                break;
                        }
                    }
                    else
                    {
                        var value = BitConverter.Int64BitsToDouble((long)ReadU64(data, result));
                        cell.Display = value.ToString(CultureInfo.InvariantCulture);
                    }
                    Track(ref maxRow, ref maxColumn, row, column);
                    break;
                }
                case StringRecord when pendingFormula is not null:
                    pendingFormula.Display = ReadInlineString(data, offset, length);
                    pendingFormula = null;
                    break;
                case BoolErr when length >= 8:
                {
                    var row = ReadU16(data, offset);
                    var column = ReadU16(data, offset + 2);
                    var value = data[offset + 6] != 0 ? "TRUE" : "FALSE";
                    Set(sheet, row, column, data[offset + 7] == 0 ? value : "#ERROR");
                    Track(ref maxRow, ref maxColumn, row, column);
                    break;
                }
            }
        }

        sheet.RowCount = Math.Max(60, maxRow + 1);
        sheet.ColumnCount = Math.Max(20, maxColumn + 1);
    }

    private static ExcelCell Set(ExcelSheet sheet, int row, int column, string text)
    {
        var cell = sheet.Ensure(row + 1, column + 1);
        cell.Display = text;
        return cell;
    }

    private static void Track(ref int maxRow, ref int maxColumn, int row, int column)
    {
        if (row > maxRow) maxRow = row;
        if (column > maxColumn) maxColumn = column;
    }

    private static void FillWidths(ExcelSheet sheet)
    {
        while (sheet.ColumnWidths.Count < sheet.ColumnCount) sheet.ColumnWidths.Add(110);
    }

    private static int FindFirstWorksheet(byte[] data)
    {
        var reader = new BiffReader(data);
        while (reader.TryRead(out var id, out var offset, out var length))
        {
            if (id != Bof || length < 4) continue;
            var type = ReadU16(data, offset + 2);
            if (type == 0x0010) return offset - 4;
        }
        return 0;
    }

    private static string ReadSheetName(byte[] data, int offset, int length)
    {
        if (length < 8) return "Sheet";
        var chars = data[offset + 6];
        var flags = data[offset + 7];
        var unicode = (flags & 0x01) != 0;
        var bytes = unicode ? chars * 2 : chars;
        if (8 + bytes > length) bytes = length - 8;
        return unicode
            ? Encoding.Unicode.GetString(data, offset + 8, bytes)
            : GetAnsi().GetString(data, offset + 8, bytes);
    }

    private static string ReadInlineString(byte[] data, int offset, int length)
    {
        if (length < 3) return string.Empty;
        var chars = ReadU16(data, offset);
        var flags = data[offset + 2];
        var unicode = (flags & 0x01) != 0;
        var bytes = unicode ? chars * 2 : chars;
        if (3 + bytes > length) bytes = Math.Max(0, length - 3);
        return unicode
            ? Encoding.Unicode.GetString(data, offset + 3, bytes)
            : GetAnsi().GetString(data, offset + 3, bytes);
    }

    private static double DecodeRk(uint value)
    {
        var divided = (value & 1) != 0;
        var integer = (value & 2) != 0;
        double result;
        if (integer)
            result = (int)value >> 2;
        else
            result = BitConverter.Int64BitsToDouble((long)(value & 0xFFFFFFFC) << 32);
        return divided ? result / 100.0 : result;
    }

    private static List<string> ReadSst(IReadOnlyList<byte[]> chunks)
    {
        var result = new List<string>();
        try
        {
            var reader = new ChunkReader(chunks);
            if (!reader.ReadU32(out _) || !reader.ReadU32(out var unique)) return result;
            for (var i = 0; i < unique; i++)
            {
                var value = reader.ReadString();
                if (value is null) break;
                result.Add(value);
            }
        }
        catch
        {
            // Return the strings parsed so far; a damaged SST should not fail the workbook.
        }
        return result;
    }

    private sealed class ChunkReader
    {
        private readonly IReadOnlyList<byte[]> _chunks;
        private int _chunk;
        private int _offset;

        public ChunkReader(IReadOnlyList<byte[]> chunks) => _chunks = chunks;

        private bool Ensure()
        {
            while (_chunk < _chunks.Count && _offset >= _chunks[_chunk].Length)
            {
                _chunk++;
                _offset = 0;
            }
            return _chunk < _chunks.Count;
        }

        public bool AtEnd => _chunk >= _chunks.Count || (_chunk == _chunks.Count - 1 && _offset >= _chunks[_chunk].Length);

        public bool ReadByte(out byte value)
        {
            value = 0;
            if (!Ensure()) return false;
            value = _chunks[_chunk][_offset++];
            return true;
        }

        public bool ReadU16(out int value)
        {
            value = 0;
            if (!ReadByte(out var low) || !ReadByte(out var high)) return false;
            value = low | high << 8;
            return true;
        }

        public bool ReadU32(out uint value)
        {
            value = 0;
            if (!ReadByte(out var b0) || !ReadByte(out var b1) || !ReadByte(out var b2) || !ReadByte(out var b3))
                return false;
            value = (uint)(b0 | b1 << 8 | b2 << 16 | b3 << 24);
            return true;
        }

        public bool MoveNextChunk(out byte flags)
        {
            flags = 0;
            if (_chunk + 1 >= _chunks.Count) return false;
            _chunk++;
            _offset = 0;
            return ReadByte(out flags);
        }

        public string? ReadString()
        {
            if (!ReadU16(out var chars) || !ReadByte(out var flags)) return null;
            var highByte = (flags & 0x01) != 0;
            var rich = (flags & 0x08) != 0;
            var extended = (flags & 0x04) != 0;
            var runs = 0;
            var extSize = 0;
            if (rich && !ReadU16(out runs)) return null;
            if (extended)
            {
                // cbExtRst is a 32-bit value stored as two little-endian words.
                if (!ReadU16(out var extLow) || !ReadU16(out var extHigh)) return null;
                extSize = extLow | extHigh << 16;
            }

            var builder = new StringBuilder(chars);
            for (var i = 0; i < chars; i++)
            {
                if (AtEnd || _offset >= _chunks[_chunk].Length)
                {
                    if (!MoveNextChunk(out var continuation)) break;
                    highByte = (continuation & 0x01) != 0;
                }
                if (highByte)
                {
                    if (!ReadByte(out var low) || !ReadByte(out var high)) break;
                    builder.Append((char)(low | high << 8));
                }
                else
                {
                    if (!ReadByte(out var value)) break;
                    builder.Append((char)value);
                }
            }

            Skip(runs * 4);
            Skip(extSize);
            return builder.ToString();
        }

        private void Skip(int count)
        {
            while (count-- > 0 && ReadByte(out _)) { }
        }
    }

    private sealed class BiffReader
    {
        private readonly byte[] _data;
        private int _position;

        public BiffReader(byte[] data, int start = 0)
        {
            _data = data;
            _position = Math.Max(0, start);
        }

        public bool TryRead(out ushort id, out int offset, out int length)
        {
            id = 0;
            offset = 0;
            length = 0;
            if (_position + 4 > _data.Length) return false;
            id = ReadU16(_data, _position);
            length = ReadU16(_data, _position + 2);
            offset = _position + 4;
            _position = offset + length;
            return offset + length <= _data.Length;
        }

        public bool Peek(out ushort id, out int offset, out int length)
        {
            var saved = _position;
            var ok = TryRead(out id, out offset, out length);
            _position = saved;
            return ok;
        }
    }

    private static byte[] Slice(byte[] data, int offset, int length)
    {
        var copy = new byte[Math.Max(0, Math.Min(length, data.Length - offset))];
        Array.Copy(data, offset, copy, 0, copy.Length);
        return copy;
    }

    private static Encoding GetAnsi()
    {
        foreach (var codePage in new[] { 936, 950, 1252 })
        {
            try { return Encoding.GetEncoding(codePage); } catch { /* next */ }
        }
        return Encoding.Latin1;
    }

    private static ushort ReadU16(byte[] data, int offset) =>
        offset >= 0 && offset + 1 < data.Length ? (ushort)(data[offset] | data[offset + 1] << 8) : (ushort)0;

    private static uint ReadU32(byte[] data, int offset) =>
        offset >= 0 && offset + 3 < data.Length
            ? (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24)
            : 0;

    private static ulong ReadU64(byte[] data, int offset) =>
        offset >= 0 && offset + 7 < data.Length
            ? ReadU32(data, offset) | (ulong)ReadU32(data, offset + 4) << 32
            : 0;
}
