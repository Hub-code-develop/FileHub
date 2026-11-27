using System.Text;

namespace FileHub.Office.Services;

/// <summary>
/// Minimal reader for OLE Compound File Binary (MS-CFB) containers.
/// Supports FAT, MiniFAT, DIFAT and the red-black directory tree. No VBA code is
/// ever executed: this type only exposes raw stream bytes to callers.
/// </summary>
public static class CompoundFileReader
{
    private const uint FreeSect = 0xFFFFFFFF;
    private const uint MaxRegSect = 0xFFFFFFFA;
    private const int HeaderSize = 512;
    private const int DirEntrySize = 128;

    private static readonly byte[] Signature =
        { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };

    /// <summary>
    /// Parses a CFB container. Keys in <paramref name="streams"/> are full paths,
    /// for example "VBA/dir" or "\u0001Ole10Native".
    /// Returns false for malformed input; <paramref name="streams"/> is always
    /// non-null and may contain the streams that were read before the failure.
    /// </summary>
    public static bool TryRead(byte[] data, out Dictionary<string, byte[]> streams)
    {
        streams = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (data is null || data.Length < HeaderSize) return false;
        for (var i = 0; i < Signature.Length; i++)
            if (data[i] != Signature[i]) return false;

        try
        {
            var sectorShift = ReadU16(data, 30);
            var sectorSize = 1 << sectorShift;
            if (sectorSize != 512 && sectorSize != 4096) return false;

            var miniSectorSize = 1 << ReadU16(data, 32);
            if (miniSectorSize <= 0) return false;

            var fatCount = ReadU32(data, 44);
            var firstDir = ReadU32(data, 48);
            var miniCutoff = ReadU32(data, 56);
            var firstMiniFat = ReadU32(data, 60);
            var miniFatCount = ReadU32(data, 64);
            var firstDifat = ReadU32(data, 68);
            var difatCount = ReadU32(data, 72);

            // --- collect FAT sector ids (header DIFAT, then DIFAT chain) ---
            var fatIds = new List<uint>();
            for (var i = 0; i < 109 && fatIds.Count < fatCount; i++)
            {
                var id = ReadU32(data, 76 + i * 4);
                if (id < MaxRegSect) fatIds.Add(id);
            }

            var difatSector = firstDifat;
            var difatGuard = 0;
            while (difatSector < MaxRegSect && difatCount > 0 && difatGuard++ < 100000)
            {
                if (!InSector(data, difatSector, sectorSize, 4)) break;
                var offset = SectorOffset(difatSector, sectorSize);
                var entriesPerDifat = sectorSize / 4 - 1;
                for (var i = 0; i < entriesPerDifat && fatIds.Count < fatCount; i++)
                {
                    var id = ReadU32(data, offset + i * 4);
                    if (id < MaxRegSect) fatIds.Add(id);
                }
                if (fatIds.Count >= fatCount) break;
                difatSector = ReadU32(data, offset + entriesPerDifat * 4);
            }

            // --- build the FAT ---
            var fat = new List<uint>();
            foreach (var id in fatIds)
            {
                if (!InSector(data, id, sectorSize, sectorSize)) continue;
                var offset = SectorOffset(id, sectorSize);
                for (var i = 0; i < sectorSize / 4; i++)
                    fat.Add(ReadU32(data, offset + i * 4));
            }
            if (fat.Count == 0) return false;

            // --- directory entries ---
            var directoryBytes = ReadChain(data, fat, firstDir, sectorSize);
            if (directoryBytes.Length < DirEntrySize) return false;
            var entries = new List<DirEntry>();
            for (var offset = 0; offset + DirEntrySize <= directoryBytes.Length; offset += DirEntrySize)
            {
                var entry = ParseDirEntry(directoryBytes, offset);
                if (entry is not null) entries.Add(entry);
            }
            if (entries.Count == 0) return false;

            var root = entries[0]; // The root storage is always the first directory entry.

            // --- MiniFAT and the mini stream (root entry's regular chain) ---
            var miniFat = new List<uint>();
            if (miniFatCount > 0 && firstMiniFat < MaxRegSect)
            {
                var miniFatBytes = ReadChain(data, fat, firstMiniFat, sectorSize);
                for (var i = 0; i + 4 <= miniFatBytes.Length; i += 4)
                    miniFat.Add(ReadU32(miniFatBytes, i));
            }
            var miniStream = root.ObjectType == 5
                ? ReadChain(data, fat, root.StartSector, sectorSize)
                : Array.Empty<byte>();

            var visited = new HashSet<int>();
            Walk(entries, root.Child, string.Empty, data, fat, miniFat, miniStream,
                 sectorSize, miniSectorSize, miniCutoff, streams, visited, 0);
            return true;
        }
        catch
        {
            // Corrupt compound files must never break the caller's Read/Save flow.
            return false;
        }
    }

    /// <summary>
    /// Parses an OLE 1.0 Native (Ole10Native) stream (MS-OLEDS 2.3.6): the real
    /// file name, its original/temporary path, and the embedded payload bytes.
    /// Supports both the common "header then payload" layout and the variant
    /// where the payload immediately follows the leading length field.
    /// </summary>
    public static byte[]? ReadOle10NativePayload(byte[] nativeStream, out string fileName, out string tempPath)
    {
        fileName = string.Empty;
        tempPath = string.Empty;
        if (nativeStream is null || nativeStream.Length < 6) return null;

        // Layout A (the layout produced by Office and parsed by oletools):
        // uint32 native size, uint16 flags, ANSI filename, ANSI source path,
        // uint32, uint32, ANSI temp path, uint32 actual size, payload.
        if (TryReadLayoutA(nativeStream, skipNativeSize: false, out var payloadA, out var nameA, out var tempA))
        {
            fileName = nameA;
            tempPath = tempA;
            return payloadA;
        }
        // Same layout without the leading native-size field (OLE package variant).
        if (TryReadLayoutA(nativeStream, skipNativeSize: true, out var payloadB, out var nameB, out var tempB))
        {
            fileName = nameB;
            tempPath = tempB;
            return payloadB;
        }
        // Layout B: uint32 payload size followed immediately by the payload,
        // then a sequence of null-terminated ANSI strings.
        return TryReadLayoutPayloadFirst(nativeStream, out fileName, out tempPath);
    }

    // ---------------------------------------------------------------- internals

    private sealed class DirEntry
    {
        public string Name = string.Empty;
        public byte ObjectType;
        public uint Left;
        public uint Right;
        public uint Child;
        public uint StartSector;
        public ulong Size;
    }

    private static void Walk(
        IReadOnlyList<DirEntry> entries,
        uint id,
        string parentPath,
        byte[] data,
        List<uint> fat,
        List<uint> miniFat,
        byte[] miniStream,
        int sectorSize,
        int miniSectorSize,
        uint miniCutoff,
        Dictionary<string, byte[]> streams,
        HashSet<int> visited,
        int depth)
    {
        if (depth > 64 || id >= (uint)entries.Count || id >= MaxRegSect) return;
        var index = (int)id;
        if (!visited.Add(index)) return;

        var entry = entries[index];
        var path = parentPath.Length == 0
            ? entry.Name
            : parentPath + "/" + entry.Name;

        Walk(entries, entry.Left, parentPath, data, fat, miniFat, miniStream,
             sectorSize, miniSectorSize, miniCutoff, streams, visited, depth + 1);

        if (entry.ObjectType == 2 && entry.Name.Length > 0)
        {
            var bytes = ReadStream(data, entry, fat, miniFat, miniStream,
                                   sectorSize, miniSectorSize, miniCutoff);
            streams[path] = bytes;
        }

        Walk(entries, entry.Right, parentPath, data, fat, miniFat, miniStream,
             sectorSize, miniSectorSize, miniCutoff, streams, visited, depth + 1);

        if (entry.ObjectType is 1 or 5)
            Walk(entries, entry.Child, path, data, fat, miniFat, miniStream,
                 sectorSize, miniSectorSize, miniCutoff, streams, visited, depth + 1);
    }

    private static DirEntry? ParseDirEntry(byte[] bytes, int offset)
    {
        if (offset < 0 || offset + DirEntrySize > bytes.Length) return null;
        var nameLength = ReadU16(bytes, offset + 64);
        if (nameLength < 2 || nameLength > 64) nameLength = 64;
        var name = Encoding.Unicode.GetString(bytes, offset, nameLength).TrimEnd('\0');
        return new DirEntry
        {
            Name = name,
            ObjectType = bytes[offset + 66],
            Left = ReadU32(bytes, offset + 68),
            Right = ReadU32(bytes, offset + 72),
            Child = ReadU32(bytes, offset + 76),
            StartSector = ReadU32(bytes, offset + 116),
            Size = ReadU64(bytes, offset + 120)
        };
    }

    private static byte[] ReadStream(
        byte[] data,
        DirEntry entry,
        List<uint> fat,
        List<uint> miniFat,
        byte[] miniStream,
        int sectorSize,
        int miniSectorSize,
        uint miniCutoff)
    {
        var size = entry.Size;
        if (size == 0 || size > int.MaxValue) return Array.Empty<byte>();
        var length = (int)size;

        if (length < miniCutoff && entry.StartSector < (uint)miniFat.Count)
        {
            var result = new byte[length];
            var written = 0;
            var sector = entry.StartSector;
            var guard = 0;
            while (sector < MaxRegSect && written < length && guard++ < 1000000)
            {
                var offset = (long)sector * miniSectorSize;
                if (offset < 0 || offset + miniSectorSize > miniStream.Length) break;
                var count = (int)Math.Min(miniSectorSize, length - written);
                Array.Copy(miniStream, (int)offset, result, written, count);
                written += count;
                sector = miniFat[(int)sector];
            }
            return written == length ? result : result.AsSpan(0, written).ToArray();
        }

        var chain = ReadChain(data, fat, entry.StartSector, sectorSize);
        if (chain.Length >= length) return chain.AsSpan(0, length).ToArray();
        var padded = new byte[length];
        Array.Copy(chain, padded, chain.Length);
        return padded;
    }

    private static byte[] ReadChain(byte[] data, List<uint> fat, uint start, int sectorSize)
    {
        using var output = new MemoryStream();
        var sector = start;
        var guard = 0;
        while (sector < MaxRegSect && guard++ < 1000000)
        {
            if (!InSector(data, sector, sectorSize, sectorSize)) break;
            var offset = SectorOffset(sector, sectorSize);
            output.Write(data, offset, sectorSize);
            if (sector >= (uint)fat.Count) break;
            sector = fat[(int)sector];
        }
        return output.ToArray();
    }

    private static bool TryReadLayoutA(
        byte[] data, bool skipNativeSize,
        out byte[] payload, out string fileName, out string tempPath)
    {
        payload = Array.Empty<byte>();
        fileName = string.Empty;
        tempPath = string.Empty;

        var position = skipNativeSize ? 0 : 4;
        if (position + 2 > data.Length) return false;
        position += 2; // unknown uint16 (object type marker)

        if (!TryReadAnsi(data, ref position, out var fileField) ||
            !TryReadAnsi(data, ref position, out var sourceField)) return false;
        if (position + 8 > data.Length) return false;
        position += 8; // two unknown uint32 values (often a FILETIME)
        if (!TryReadAnsi(data, ref position, out var tempField)) return false;
        if (position + 4 > data.Length) return false;

        var actualSize = ReadU32(data, position);
        position += 4;
        if (actualSize > (uint)(data.Length - position)) return false;
        if (string.IsNullOrEmpty(fileField) && string.IsNullOrEmpty(tempField) &&
            string.IsNullOrEmpty(sourceField)) return false;

        payload = data.AsSpan(position, (int)actualSize).ToArray();
        fileName = SafeFileName(fileField);
        tempPath = !string.IsNullOrEmpty(tempField) ? tempField : sourceField;
        return true;
    }

    private static byte[]? TryReadLayoutPayloadFirst(byte[] data, out string fileName, out string tempPath)
    {
        fileName = string.Empty;
        tempPath = string.Empty;
        var payloadSize = ReadU32(data, 0);
        if (payloadSize == 0 || payloadSize > (uint)(data.Length - 4)) return null;
        var payload = data.AsSpan(4, (int)payloadSize).ToArray();
        var position = 4 + (int)payloadSize;

        var strings = new List<string>();
        for (var i = 0; i < 8 && position < data.Length; i++)
        {
            if (!TryReadAnsi(data, ref position, out var value)) break;
            if (value.Length > 0) strings.Add(value);
        }
        if (strings.Count == 0) return null;

        fileName = SafeFileName(strings[0]);
        tempPath = strings.FirstOrDefault(s => s.Contains('\\') || s.Contains('/'))
                   ?? strings.ElementAtOrDefault(1) ?? string.Empty;
        return payload;
    }

    private static bool TryReadAnsi(byte[] data, ref int position, out string value)
    {
        value = string.Empty;
        if (position < 0 || position >= data.Length) return false;
        var limit = Math.Min(data.Length, position + 1024);
        var end = Array.IndexOf(data, (byte)0, position, limit - position);
        if (end < 0) return false;
        value = Encoding.Latin1.GetString(data, position, end - position);
        position = end + 1;
        return true;
    }

    private static string SafeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "embedded.bin";
        var normalised = value.Replace('\\', '/');
        var name = normalised[(normalised.LastIndexOf('/') + 1)..].Trim();
        if (name.Length == 0) return "embedded.bin";
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return builder.ToString();
    }

    private static bool InSector(byte[] data, uint sector, int sectorSize, int need)
    {
        var offset = 512L + (long)sector * sectorSize;
        return offset >= 0 && offset + need <= data.Length;
    }

    private static int SectorOffset(uint sector, int sectorSize) =>
        HeaderSize + (int)sector * sectorSize;

    private static ushort ReadU16(byte[] data, int offset)
    {
        if (offset < 0 || offset + 2 > data.Length) return 0;
        return (ushort)(data[offset] | (data[offset + 1] << 8));
    }

    private static uint ReadU32(byte[] data, int offset)
    {
        if (offset < 0 || offset + 4 > data.Length) return FreeSect;
        return (uint)(data[offset]
                      | (data[offset + 1] << 8)
                      | (data[offset + 2] << 16)
                      | (data[offset + 3] << 24));
    }

    private static ulong ReadU64(byte[] data, int offset) =>
        ReadU32(data, offset) | ((ulong)ReadU32(data, offset + 4) << 32);
}
