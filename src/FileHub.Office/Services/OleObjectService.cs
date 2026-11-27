using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using FileHub.Office.Models;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace FileHub.Office.Services;

/// <summary>
/// Reads, extracts and adds OLE objects embedded in Office packages.
/// Embedded payloads are treated as opaque data; no object is ever activated.
/// </summary>
public static class OleObjectService
{
    private const string OleObjectContentType =
        "application/vnd.openxmlformats-officedocument.oleObject";
    private const string NativeStreamName = "\u0001Ole10Native";
    private const string CompObjStreamName = "\u0001CompObj";
    private const string OleStreamName = "\u0001Ole";

    /// <summary>Populates <see cref="OfficeDocument.EmbeddedObjects"/> from the package.</summary>
    public static void Read(OfficeDocument doc)
    {
        if (doc is null) return;
        doc.EmbeddedObjects.Clear();
        if (string.IsNullOrWhiteSpace(doc.Path) || !File.Exists(doc.Path)) return;

        try
        {
            using OpenXmlPackage package = doc.Kind switch
            {
                OfficeDocumentKind.Excel => SpreadsheetDocument.Open(doc.Path, false),
                OfficeDocumentKind.PowerPoint => PresentationDocument.Open(doc.Path, false),
                _ => WordprocessingDocument.Open(doc.Path, false)
            };

            foreach (var part in EnumerateParts(package))
            {
                if (part is not (EmbeddedObjectPart or EmbeddedPackagePart)) continue;
                byte[] data;
                using (var stream = part.GetStream(FileMode.Open, FileAccess.Read))
                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    data = memory.ToArray();
                }
                doc.EmbeddedObjects.Add(BuildObject(part, data));
            }
        }
        catch
        {
            // A damaged package must not break the document Read pipeline.
        }
    }

    /// <summary>
    /// Integration hook invoked after the host document is saved. Ensures every
    /// model object still has a binary part in the package; parts written by
    /// AddPackage are already present and are never overwritten here.
    /// </summary>
    public static void Sync(OfficeDocument doc)
    {
        if (doc is null || doc.EmbeddedObjects.Count == 0 ||
            string.IsNullOrWhiteSpace(doc.Path) || !File.Exists(doc.Path)) return;
        try
        {
            var kind = doc.Kind == OfficeDocumentKind.Unknown
                ? OfficeDocumentKind.Word : doc.Kind;
            using OpenXmlPackage package = kind switch
            {
                OfficeDocumentKind.Excel => SpreadsheetDocument.Open(doc.Path, true),
                OfficeDocumentKind.PowerPoint => PresentationDocument.Open(doc.Path, true),
                _ => WordprocessingDocument.Open(doc.Path, true)
            };

            var existing = new HashSet<string>(
                EnumerateParts(package).Select(p => p.Uri?.OriginalString ?? string.Empty),
                StringComparer.OrdinalIgnoreCase);
            var parent = GetEmbeddingParent(package, kind);
            if (parent is null) return;

            foreach (var obj in doc.EmbeddedObjects)
            {
                if (obj.Data is null || obj.Data.Length == 0 ||
                    string.IsNullOrEmpty(obj.PartUri) || existing.Contains(obj.PartUri)) continue;
                var part = parent.AddNewPart<EmbeddedPackagePart>(OleObjectContentType, null);
                part.FeedData(new MemoryStream(obj.Data));
                obj.PartUri = part.Uri?.OriginalString ?? obj.PartUri;
            }
        }
        catch
        {
            // Synchronisation is best effort and must not fail the host save.
        }
    }

    /// <summary>Writes an object's payload under the temp directory and returns its path.</summary>
    public static string Extract(OfficeEmbeddedObject obj)
    {
        if (obj is null) return string.Empty;
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "filehub-ole");
            Directory.CreateDirectory(directory);
            var name = SanitizeFileName(string.IsNullOrWhiteSpace(obj.ExtractedFileName)
                ? obj.Name : obj.ExtractedFileName);
            var path = Path.Combine(directory, name);
            File.WriteAllBytes(path, obj.Data ?? Array.Empty<byte>());
            return path;
        }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Embeds a real file as an OLE Package. The embedded binary part is a
    /// compound file with an Ole10Native payload, a display name and the
    /// "Package" ProgId; the original file bytes are preserved verbatim.
    /// </summary>
    public static void AddPackage(OfficeDocument doc, string filePath, string displayName)
    {
        if (doc is null || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;
        try
        {
            var payload = File.ReadAllBytes(filePath);
            var display = string.IsNullOrWhiteSpace(displayName)
                ? Path.GetFileName(filePath) : displayName;
            var safeName = SanitizeFileName(display);

            var native = BuildOle10Native(payload, safeName, filePath);
            var compObj = BuildCompObj();
            var ole = BuildOleStream();

            byte[] embedded;
            try
            {
                embedded = CfbWriter.Write(new[]
                {
                    (NativeStreamName, native),
                    (CompObjStreamName, compObj),
                    (OleStreamName, ole)
                });
            }
            catch
            {
                // Extremely large payloads may not fit the compact writer's DIFAT.
                embedded = payload;
            }

            var partUri = AddEmbeddedPart(doc, embedded);
            if (partUri is null) return;

            doc.EmbeddedObjects.Add(new OfficeEmbeddedObject
            {
                Name = safeName,
                ProgId = "Package",
                ContentType = OleObjectContentType,
                PartUri = partUri,
                Data = payload,
                ExtractedFileName = safeName,
                IsPackage = true,
                DocumentName = filePath
            });
        }
        catch
        {
            // Best effort: embedding failures never corrupt the host document.
        }
    }

    // ---------------------------------------------------------------- internals

    private static OfficeEmbeddedObject BuildObject(OpenXmlPart part, byte[] data)
    {
        var uri = part.Uri?.OriginalString ?? string.Empty;
        var fallbackName = SanitizeFileName(Path.GetFileName(uri.Replace('/', '\\')));
        var obj = new OfficeEmbeddedObject
        {
            Name = fallbackName,
            ContentType = part.ContentType,
            PartUri = uri,
            Data = data,
            ExtractedFileName = fallbackName
        };

        if (CompoundFileReader.TryRead(data, out var streams))
        {
            var nativeKey = streams.Keys.FirstOrDefault(k =>
                k.Equals(NativeStreamName, StringComparison.OrdinalIgnoreCase));
            if (nativeKey is not null)
            {
                var payload = CompoundFileReader.ReadOle10NativePayload(
                    streams[nativeKey], out var fileName, out var tempPath);
                if (payload is not null)
                {
                    obj.Data = payload;
                    obj.ExtractedFileName = fileName;
                    obj.Name = fileName;
                    obj.DocumentName = tempPath;
                    obj.IsPackage = true;
                    obj.ProgId = "Package";
                    obj.ContentType = GuessContentType(fileName);
                }
            }

            var compKey = streams.Keys.FirstOrDefault(k =>
                k.Equals(CompObjStreamName, StringComparison.OrdinalIgnoreCase));
            if (compKey is not null)
            {
                var progId = FindProgId(streams[compKey]);
                if (!string.IsNullOrEmpty(progId)) obj.ProgId = progId;
            }
        }
        return obj;
    }

    private static string? AddEmbeddedPart(OfficeDocument doc, byte[] embedded)
    {
        var kind = doc.Kind == OfficeDocumentKind.Unknown
            ? OfficeDocumentKind.Word : doc.Kind;
        var exists = File.Exists(doc.Path);
        if (!exists) CreateMinimalPackage(doc.Path, kind);

        OpenXmlPackage package = kind switch
        {
            OfficeDocumentKind.Excel => SpreadsheetDocument.Open(doc.Path, true),
            OfficeDocumentKind.PowerPoint => PresentationDocument.Open(doc.Path, true),
            _ => WordprocessingDocument.Open(doc.Path, true)
        };

        using (package)
        {
            var parent = GetEmbeddingParent(package, kind);
            if (parent is null) return null;

            // EmbeddedPackagePart has no fixed content type, so provide it explicitly.
            var part = parent.AddNewPart<EmbeddedPackagePart>(OleObjectContentType, null);
            part.FeedData(new MemoryStream(embedded));
            return part.Uri?.OriginalString;
        }
    }

    private static void CreateMinimalPackage(string path, OfficeDocumentKind kind)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        switch (kind)
        {
            case OfficeDocumentKind.Excel:
                using (var workbook = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
                {
                    var part = workbook.AddWorkbookPart();
                    part.Workbook = new S.Workbook(new S.Sheets());
                    part.Workbook.Save();
                }
                break;
            case OfficeDocumentKind.PowerPoint:
                using (var presentation = PresentationDocument.Create(
                           path, PresentationDocumentType.Presentation))
                {
                    var part = presentation.AddPresentationPart();
                    part.Presentation = new P.Presentation(new P.SlideIdList());
                    part.Presentation.Save();
                }
                break;
            default:
                using (var word = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
                {
                    var part = word.AddMainDocumentPart();
                    part.Document = new W.Document(new W.Body(new W.Paragraph()));
                    part.Document.Save();
                }
                break;
        }
    }

    // EmbeddedPackagePart can only be related from containers that declare it
    // (main document, worksheet, slide, and the other story/masters parts).
    private static OpenXmlPartContainer? GetEmbeddingParent(
        OpenXmlPackage package, OfficeDocumentKind kind)
    {
        return kind switch
        {
            OfficeDocumentKind.Excel =>
                ((SpreadsheetDocument)package).WorkbookPart?.WorksheetParts.FirstOrDefault(),
            OfficeDocumentKind.PowerPoint =>
                ((PresentationDocument)package).PresentationPart?.SlideParts.FirstOrDefault(),
            _ => ((WordprocessingDocument)package).MainDocumentPart
        };
    }

    private static IEnumerable<OpenXmlPart> EnumerateParts(OpenXmlPackage package)
    {
        var visited = new HashSet<OpenXmlPart>();
        var pending = new Stack<OpenXmlPart>();
        foreach (var pair in package.Parts) pending.Push(pair.OpenXmlPart);

        while (pending.Count > 0)
        {
            var part = pending.Pop();
            if (!visited.Add(part)) continue;
            yield return part;
            try
            {
                foreach (var child in part.Parts) pending.Push(child.OpenXmlPart);
            }
            catch
            {
                // Ignore containers whose children are not enumerable.
            }
        }
    }

    private static byte[] BuildOle10Native(byte[] payload, string fileName, string sourcePath)
    {
        using var stream = new MemoryStream();
        WriteU32(stream, (uint)payload.Length);
        WriteU16(stream, 0x0002); // embedded (as opposed to linked)
        WriteAnsi(stream, fileName);
        WriteAnsi(stream, sourcePath);
        WriteU32(stream, 0);
        WriteU32(stream, 0);
        WriteAnsi(stream, Path.Combine(Path.GetTempPath(), fileName));
        WriteU32(stream, (uint)payload.Length);
        stream.Write(payload, 0, payload.Length);
        return stream.ToArray();
    }

    private static byte[] BuildCompObj()
    {
        using var stream = new MemoryStream();
        WriteU32(stream, 0x00000001);
        WriteU32(stream, 0xFFFFFFFE);
        WriteU32(stream, 0x00000003);
        WriteU32(stream, 0x00000000);
        stream.Write(PackageClsid, 0, PackageClsid.Length);
        WriteU32(stream, 0x00000004);
        WriteAnsi(stream, "Package");
        return stream.ToArray();
    }

    private static byte[] BuildOleStream()
    {
        using var stream = new MemoryStream();
        WriteU16(stream, 0x0002);
        WriteU16(stream, 0x0000);
        WriteU16(stream, 0x0000);
        WriteU16(stream, 0x0000);
        stream.Write(PackageClsid, 0, PackageClsid.Length);
        return stream.ToArray();
    }

    // CLSID {00030000-0000-0000-C000-000000000046} in CFB byte order.
    private static readonly byte[] PackageClsid =
    {
        0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00,
        0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46
    };

    private static void WriteU16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)value);
        stream.WriteByte((byte)(value >> 8));
    }

    private static void WriteU32(Stream stream, uint value)
    {
        stream.WriteByte((byte)value);
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 24));
    }

    private static void WriteAnsi(Stream stream, string value)
    {
        var bytes = Encoding.Latin1.GetBytes(value ?? string.Empty);
        stream.Write(bytes, 0, bytes.Length);
        stream.WriteByte(0);
    }

    private static string FindProgId(byte[] compObj)
    {
        var text = Encoding.Latin1.GetString(compObj);
        var token = new StringBuilder();
        for (var i = 0; i <= text.Length; i++)
        {
            var c = i < text.Length ? text[i] : '\0';
            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
            {
                token.Append(c);
                continue;
            }
            if (token.Length >= 4 && token.Length <= 128)
            {
                var candidate = token.ToString();
                if (candidate.Equals("Package", StringComparison.OrdinalIgnoreCase) ||
                    (candidate.Contains('.') && candidate.Length < 64))
                    return candidate;
            }
            token.Clear();
        }
        return string.Empty;
    }

    private static string GuessContentType(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".txt" or ".csv" or ".log" or ".md" => "text/plain",
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".zip" => "application/zip",
            ".doc" or ".docm" or ".docx" => "application/msword",
            ".xls" or ".xlsx" or ".xlsm" => "application/vnd.ms-excel",
            ".ppt" or ".pptx" or ".pptm" => "application/vnd.ms-powerpoint",
            _ => "application/octet-stream"
        };
    }

    private static string SanitizeFileName(string value)
    {
        var candidate = string.IsNullOrWhiteSpace(value) ? "embedded.bin" : value.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(candidate.Length);
        foreach (var c in candidate)
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        var result = builder.ToString().Trim();
        if (result.Length == 0) result = "embedded.bin";
        return result.Length > 120 ? result[..120] : result;
    }

    /// <summary>
    /// Compact MS-CFB v3 writer used for small OLE packages. Streams below the
    /// 4096-byte cutoff go to the MiniFAT; larger streams use regular sectors.
    /// </summary>
    private static class CfbWriter
    {
        private const uint Free = 0xFFFFFFFF;
        private const uint End = 0xFFFFFFFE;
        private const uint FatSect = 0xFFFFFFFD;
        private const int SectorSize = 512;
        private const int MiniSize = 64;
        private const int Cutoff = 4096;
        private const int EntrySize = 128;

        private sealed class Item
        {
            public string Name = string.Empty;
            public byte[] Data = Array.Empty<byte>();
            public bool UseMini;
            public uint Start;
            public uint Left;
            public uint Right;
            public int DirIndex;
        }

        public static byte[] Write(IReadOnlyList<(string Name, byte[] Data)> streams)
        {
            if (streams.Count == 0) throw new ArgumentException("No streams to write.", nameof(streams));

            var items = streams.Select(s => new Item
            {
                Name = s.Name,
                Data = s.Data,
                UseMini = s.Data.Length > 0 && s.Data.Length < Cutoff
            }).ToList();
            for (var i = 0; i < items.Count; i++) items[i].DirIndex = i + 1;

            var sectors = new List<byte[]>();
            var fat = new List<uint>();

            uint Allocate(byte[] content)
            {
                var id = (uint)sectors.Count;
                sectors.Add(content);
                fat.Add(Free);
                return id;
            }

            uint AllocateChain(byte[] data)
            {
                if (data.Length == 0) return End;
                var ids = new List<uint>();
                for (var offset = 0; offset < data.Length; offset += SectorSize)
                {
                    var block = new byte[SectorSize];
                    var count = Math.Min(SectorSize, data.Length - offset);
                    Array.Copy(data, offset, block, 0, count);
                    ids.Add(Allocate(block));
                }
                for (var i = 0; i < ids.Count; i++)
                    fat[(int)ids[i]] = i == ids.Count - 1 ? End : ids[i + 1];
                return ids[0];
            }

            // Mini stream and its MiniFAT chain.
            var miniStream = new List<byte>();
            var miniFat = new List<uint>();
            foreach (var item in items.Where(i => i.UseMini))
            {
                item.Start = (uint)(miniStream.Count / MiniSize);
                var count = (item.Data.Length + MiniSize - 1) / MiniSize;
                for (var i = 0; i < count; i++)
                    miniFat.Add(i == count - 1 ? End : item.Start + (uint)i + 1);
                miniStream.AddRange(item.Data);
                while (miniStream.Count % MiniSize != 0) miniStream.Add(0);
            }
            var miniStreamStart = AllocateChain(miniStream.ToArray());

            // Regular streams.
            foreach (var item in items.Where(i => !i.UseMini))
                item.Start = AllocateChain(item.Data);

            // Directory: root first, then streams in insertion order.
            var sorted = items.OrderBy(i => i.Name.Length).ThenBy(i => i.Name, StringComparer.Ordinal).ToList();
            var rootChild = BuildTree(sorted, 0, sorted.Count - 1);
            var directory = new byte[(items.Count + 1) * EntrySize];
            Array.Copy(CreateEntry("Root Entry", 5, End, End, rootChild, miniStreamStart,
                (ulong)miniStream.Count), 0, directory, 0, EntrySize);
            foreach (var item in items)
            {
                var entry = CreateEntry(item.Name, 2, item.Left, item.Right, End,
                    item.Start, (ulong)item.Data.Length);
                Array.Copy(entry, 0, directory, item.DirIndex * EntrySize, EntrySize);
            }
            var directoryStart = AllocateChain(directory);

            var miniFatBytes = PackFat(miniFat);
            var miniFatStart = AllocateChain(miniFatBytes);
            var miniFatCount = miniFatBytes.Length / SectorSize;

            // The FAT must account for its own sectors as well.
            var fatSectorCount = 1;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var needed = (sectors.Count + fatSectorCount + 127) / 128;
                if (needed <= fatSectorCount) break;
                fatSectorCount = needed;
            }
            var fatSectorIds = new List<uint>();
            for (var i = 0; i < fatSectorCount; i++)
            {
                var id = Allocate(new byte[SectorSize]);
                fat[(int)id] = FatSect;
                fatSectorIds.Add(id);
            }

            while (fat.Count < fatSectorCount * 128) fat.Add(Free);
            var packedFat = new byte[fatSectorCount * SectorSize];
            for (var i = 0; i < fat.Count && i < fatSectorCount * 128; i++)
            {
                var value = fat[i];
                packedFat[i * 4] = (byte)value;
                packedFat[i * 4 + 1] = (byte)(value >> 8);
                packedFat[i * 4 + 2] = (byte)(value >> 16);
                packedFat[i * 4 + 3] = (byte)(value >> 24);
            }
            for (var i = 0; i < fatSectorIds.Count; i++)
                Array.Copy(packedFat, i * SectorSize, sectors[(int)fatSectorIds[i]], 0, SectorSize);

            var header = new byte[512];
            byte[] signature = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };
            Array.Copy(signature, header, signature.Length);
            WriteHeaderU16(header, 24, 0x003E); // minor version
            WriteHeaderU16(header, 26, 3);      // major version
            WriteHeaderU16(header, 28, 0xFFFE); // byte order
            WriteHeaderU16(header, 30, 9);      // 512-byte sectors
            WriteHeaderU16(header, 32, 6);      // 64-byte mini sectors
            WriteHeaderU32(header, 44, (uint)fatSectorCount);
            WriteHeaderU32(header, 48, directoryStart);
            WriteHeaderU32(header, 56, Cutoff);
            WriteHeaderU32(header, 60, miniFatStart);
            WriteHeaderU32(header, 64, (uint)miniFatCount);
            WriteHeaderU32(header, 68, End);
            WriteHeaderU32(header, 72, 0);
            for (var i = 0; i < 109; i++)
                WriteHeaderU32(header, 76 + i * 4,
                    i < fatSectorIds.Count ? fatSectorIds[i] : Free);

            var result = new byte[512 + sectors.Count * SectorSize];
            Array.Copy(header, result, 512);
            for (var i = 0; i < sectors.Count; i++)
                Array.Copy(sectors[i], 0, result, 512 + i * SectorSize, SectorSize);
            return result;
        }

        private static uint BuildTree(List<Item> sorted, int low, int high)
        {
            if (low > high) return End;
            var middle = (low + high) / 2;
            var item = sorted[middle];
            item.Left = BuildTree(sorted, low, middle - 1);
            item.Right = BuildTree(sorted, middle + 1, high);
            return (uint)item.DirIndex;
        }

        private static byte[] CreateEntry(string name, byte type, uint left, uint right,
            uint child, uint start, ulong size)
        {
            var entry = new byte[EntrySize];
            var nameBytes = Encoding.Unicode.GetBytes(name + "\0");
            Array.Copy(nameBytes, 0, entry, 0, Math.Min(nameBytes.Length, 64));
            WriteHeaderU16(entry, 64, (ushort)nameBytes.Length);
            entry[66] = type;
            entry[67] = 1; // black
            WriteHeaderU32(entry, 68, left);
            WriteHeaderU32(entry, 72, right);
            WriteHeaderU32(entry, 76, child);
            WriteHeaderU32(entry, 116, start);
            WriteHeaderU32(entry, 120, (uint)size);
            WriteHeaderU32(entry, 124, (uint)(size >> 32));
            return entry;
        }

        private static byte[] PackFat(List<uint> entries)
        {
            if (entries.Count == 0) return Array.Empty<byte>();
            var sectorCount = (entries.Count + 127) / 128;
            var bytes = new byte[sectorCount * SectorSize];
            for (var i = 0; i < entries.Count; i++)
            {
                bytes[i * 4] = (byte)entries[i];
                bytes[i * 4 + 1] = (byte)(entries[i] >> 8);
                bytes[i * 4 + 2] = (byte)(entries[i] >> 16);
                bytes[i * 4 + 3] = (byte)(entries[i] >> 24);
            }
            for (var i = entries.Count; i < bytes.Length / 4; i++)
            {
                bytes[i * 4] = 0xFF;
                bytes[i * 4 + 1] = 0xFF;
                bytes[i * 4 + 2] = 0xFF;
                bytes[i * 4 + 3] = 0xFF;
            }
            return bytes;
        }

        private static void WriteHeaderU16(byte[] bytes, int offset, ushort value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
        }

        private static void WriteHeaderU32(byte[] bytes, int offset, uint value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
            bytes[offset + 2] = (byte)(value >> 16);
            bytes[offset + 3] = (byte)(value >> 24);
        }

    }
}
