using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using FileHub.Office.Models;

namespace FileHub.Office.Services;

/// <summary>
/// Detects and inspects VBA projects. VBA source is only decompressed for
/// display: this service never runs, evaluates or compiles macro code.
/// </summary>
public static class MacroService
{
    private static readonly object SyncRoot = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static MacroSecurityLevel? _cachedSecurity;

    private static string ConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".filehub");

    private static string SecurityPath => Path.Combine(ConfigDirectory, "macro-security.json");

    private static string RecordingsPath => Path.Combine(ConfigDirectory, "macros.json");

    /// <summary>Persisted macro trust level. Defaults to Prompt.</summary>
    public static MacroSecurityLevel SecurityLevel
    {
        get
        {
            lock (SyncRoot)
            {
                if (_cachedSecurity.HasValue) return _cachedSecurity.Value;
                var level = MacroSecurityLevel.Prompt;
                try
                {
                    if (File.Exists(SecurityPath))
                    {
                        using var document = JsonDocument.Parse(File.ReadAllText(SecurityPath));
                        if (document.RootElement.TryGetProperty("level", out var element))
                        {
                            if (element.ValueKind == JsonValueKind.String &&
                                Enum.TryParse(element.GetString(), ignoreCase: true, out MacroSecurityLevel parsed))
                                level = parsed;
                            else if (element.ValueKind == JsonValueKind.Number &&
                                     element.TryGetInt32(out var number) &&
                                     Enum.IsDefined(typeof(MacroSecurityLevel), number))
                                level = (MacroSecurityLevel)number;
                        }
                    }
                }
                catch
                {
                    // Broken settings must not prevent the application from starting.
                }
                _cachedSecurity = level;
                return level;
            }
        }
        set
        {
            lock (SyncRoot)
            {
                _cachedSecurity = value;
                try
                {
                    Directory.CreateDirectory(ConfigDirectory);
                    File.WriteAllText(SecurityPath,
                        JsonSerializer.Serialize(new { level = value.ToString() }, JsonOptions));
                }
                catch
                {
                    // Best-effort persistence; the in-memory value still applies.
                }
            }
        }
    }

    /// <summary>
    /// Reads the vbaProject.bin / vbProject.bin part, decompresses module streams
    /// and fills <see cref="OfficeDocument.MacroProject"/>.
    /// </summary>
    public static void Read(OfficeDocument doc)
    {
        if (doc is null) return;
        var project = doc.MacroProject;
        project.HasVbaProject = false;
        project.ProjectName = string.Empty;
        project.Version = string.Empty;
        project.Warning = string.Empty;
        project.Modules.Clear();
        doc.PreserveVbaProject = false;

        if (string.IsNullOrWhiteSpace(doc.Path) || !File.Exists(doc.Path)) return;

        try
        {
            var bin = FindVbaProjectBytes(doc);
            if (bin is null || bin.Length == 0) return;

            project.HasVbaProject = true;
            doc.PreserveVbaProject = true; // Save keeps the part; no VBA execution is performed.

            if (!CompoundFileReader.TryRead(bin, out var streams))
            {
                project.Warning = "The VBA project is not a valid compound file.";
                return;
            }

            var dirKey = streams.Keys.FirstOrDefault(k =>
                k.Equals("dir", StringComparison.OrdinalIgnoreCase) ||
                k.EndsWith("/dir", StringComparison.OrdinalIgnoreCase));
            if (dirKey is null)
            {
                project.Warning = "The VBA project has no dir stream.";
                return;
            }

            var dir = DecompressVba(streams[dirKey]);
            ParseDirectory(dir, streams, project);
        }
        catch (Exception ex)
        {
            project.HasVbaProject = true;
            doc.PreserveVbaProject = true;
            project.Warning = "VBA project inspection failed: " + ex.Message;
        }
    }

    /// <summary>True when the document carries a VBA project.</summary>
    public static bool HasVba(OfficeDocument doc)
    {
        if (doc is null) return false;
        if (doc.MacroProject.HasVbaProject || doc.PreserveVbaProject ||
            doc.MacroProject.Modules.Count > 0) return true;
        if (string.IsNullOrWhiteSpace(doc.Path) || !File.Exists(doc.Path)) return false;
        try { return FindVbaProjectBytes(doc) is not null; }
        catch { return false; }
    }

    /// <summary>Loads all saved FileHub macro recordings.</summary>
    public static IReadOnlyList<MacroRecording> LoadRecordings()
    {
        lock (SyncRoot) return LoadRecordingsCore();
    }

    /// <summary>Inserts a recording, replacing a previous one with the same name.</summary>
    public static void SaveRecording(MacroRecording recording)
    {
        if (recording is null) return;
        lock (SyncRoot)
        {
            var items = LoadRecordingsCore().ToList();
            items.RemoveAll(r => string.Equals(r.Name, recording.Name, StringComparison.OrdinalIgnoreCase));
            items.Add(recording);
            WriteRecordingsCore(items);
        }
    }

    /// <summary>Deletes recordings by name (case-insensitive).</summary>
    public static void DeleteRecording(string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        lock (SyncRoot)
        {
            var items = LoadRecordingsCore()
                .Where(r => !string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            WriteRecordingsCore(items);
        }
    }

    /// <summary>
    /// Decompresses an MS-OVBA 2.4.1 compressed container. Returns an empty
    /// array for malformed data; no call into this method executes VBA.
    /// </summary>
    public static byte[] DecompressVba(byte[] compressed)
    {
        if (compressed is null || compressed.Length < 2 || compressed[0] != 0x01)
            return Array.Empty<byte>();

        var output = new List<byte>(4096);
        const int maxOutput = 32 * 1024 * 1024;
        var position = 1;

        while (position + 1 < compressed.Length && output.Count < maxOutput)
        {
            var chunkStart = position;
            var header = (ushort)(compressed[position] | (compressed[position + 1] << 8));
            var chunkSize = (header & 0x0FFF) + 3; // includes the 2-byte chunk header
            var compressedFlag = (header >> 15) & 1;
            var chunkEnd = Math.Min(compressed.Length, chunkStart + chunkSize);
            position += 2;
            if (chunkEnd <= position) break;

            if (compressedFlag == 0)
            {
                // RawChunk: up to 4096 literal bytes.
                var count = Math.Min(4096, chunkEnd - position);
                for (var i = 0; i < count && output.Count < maxOutput; i++)
                    output.Add(compressed[position++]);
                position = chunkEnd;
                continue;
            }

            var chunkOutputStart = output.Count;
            while (position < chunkEnd && output.Count < maxOutput)
            {
                var flagByte = compressed[position++];
                for (var bit = 0; bit < 8 && position < chunkEnd && output.Count < maxOutput; bit++)
                {
                    if ((flagByte & (1 << bit)) == 0)
                    {
                        // LiteralToken: one plain byte.
                        output.Add(compressed[position++]);
                        continue;
                    }

                    // CopyToken: variable split between offset and length bits.
                    // The offset width grows with the number of bytes already
                    // decompressed in this chunk (MS-OVBA 2.4.1.3.19.1).
                    if (position + 1 >= chunkEnd) { position = chunkEnd; break; }
                    var token = (ushort)(compressed[position] | (compressed[position + 1] << 8));
                    position += 2;

                    var produced = output.Count - chunkOutputStart;
                    var offsetBits = Math.Max(4, CeilLog2(Math.Max(1, produced)));
                    var lengthBits = 16 - offsetBits;
                    var length = (token & ((1 << lengthBits) - 1)) + 3;
                    var offset = (token >> lengthBits) + 1;
                    var source = output.Count - offset;
                    if (source < 0 || source >= output.Count) continue;

                    // Byte-wise copy: the source may overlap the destination.
                    for (var i = 0; i < length && output.Count < maxOutput; i++)
                    {
                        if (source + i >= output.Count) break;
                        output.Add(output[source + i]);
                    }
                }
            }
            position = chunkEnd;
        }

        return output.ToArray();
    }

    // ---------------------------------------------------------------- internals

    private static int CeilLog2(int value)
    {
        var bits = 0;
        while ((1 << bits) < value) bits++;
        return bits;
    }

    private static byte[]? FindVbaProjectBytes(OfficeDocument doc)
    {
        using OpenXmlPackage package = doc.Kind switch
        {
            OfficeDocumentKind.Excel => SpreadsheetDocument.Open(doc.Path, false),
            OfficeDocumentKind.PowerPoint => PresentationDocument.Open(doc.Path, false),
            _ => WordprocessingDocument.Open(doc.Path, false)
        };

        foreach (var part in EnumerateParts(package))
        {
            var uri = part.Uri?.OriginalString ?? string.Empty;
            if (part is VbaProjectPart ||
                uri.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase) ||
                uri.EndsWith("vbProject.bin", StringComparison.OrdinalIgnoreCase) ||
                part.ContentType.Contains("vbaProject", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                return memory.ToArray();
            }
        }
        return null;
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
                // Some part implementations cannot enumerate children; ignore them.
            }
        }
    }

    private sealed record ModuleStub(
        string Name, string StreamName, int Offset, bool IsDocument);

    private static void ParseDirectory(byte[] dir, Dictionary<string, byte[]> streams, MacroProjectInfo project)
    {
        if (dir.Length == 0) return;
        project.ProjectName = FindSizedRecord(dir, 0x0004, 1, 128) is { } nameBytes
            ? DecodeText(nameBytes, 1252) : string.Empty;
        project.Version = FindVersion(dir);

        var codePage = 1252;
        if (FindSizedRecord(dir, 0x0003, 2, 2) is { } codeBytes && codeBytes.Length == 2)
            codePage = codeBytes[0] | (codeBytes[1] << 8);

        var modules = ParseModuleRecords(dir);
        foreach (var stub in modules)
        {
            var module = new MacroModule
            {
                Name = stub.Name,
                StreamName = stub.StreamName,
                IsDocumentModule = stub.IsDocument
            };

            var key = streams.Keys.FirstOrDefault(k =>
                k.Equals("VBA/" + stub.StreamName, StringComparison.OrdinalIgnoreCase) ||
                k.EndsWith("/" + stub.StreamName, StringComparison.OrdinalIgnoreCase));
            if (key is not null && streams.TryGetValue(key, out var raw))
            {
                var sourceBytes = raw.Length > 0 && raw[0] == 0x01 ? DecompressVba(raw) : raw;
                if (stub.Offset > 0 && stub.Offset < sourceBytes.Length)
                    sourceBytes = sourceBytes[stub.Offset..];
                module.Source = DecodeText(sourceBytes, codePage);
            }
            project.Modules.Add(module);
        }
    }

    private static byte[]? FindSizedRecord(byte[] dir, ushort id, int minSize, int maxSize)
    {
        for (var i = 0; i + 6 <= dir.Length; i++)
        {
            if (ReadU16(dir, i) != id) continue;
            var size = ReadU32(dir, i + 2);
            if (size < minSize || size > maxSize || i + 6 + (int)size > dir.Length) continue;
            return dir.AsSpan(i + 6, (int)size).ToArray();
        }
        return null;
    }

    private static string FindVersion(byte[] dir)
    {
        for (var i = 0; i + 12 <= dir.Length; i++)
        {
            // PROJECTVERSION: id(2), reserved(4) == 4, major(4), minor(2).
            if (ReadU16(dir, i) == 0x0009 && ReadU32(dir, i + 2) == 4)
                return ReadU32(dir, i + 6) + "." + ReadU16(dir, i + 10);
        }
        return string.Empty;
    }

    private static List<ModuleStub> ParseModuleRecords(byte[] dir)
    {
        var result = new List<ModuleStub>();
        var start = -1;
        for (var i = 0; i + 6 <= dir.Length; i++)
        {
            if (ReadU16(dir, i) != 0x0019) continue; // MODULENAME
            var size = ReadU32(dir, i + 2);
            if (size < 1 || size > 128 || i + 6 + (int)size > dir.Length) continue;
            start = i;
            break;
        }
        if (start < 0) return result;

        var position = start;
        ModuleStub? current = null;
        while (position + 2 <= dir.Length && result.Count < 512)
        {
            var id = ReadU16(dir, position);
            position += 2;

            if (id == 0x002B) // MODULETERMINATOR
            {
                if (current is not null && current.Name.Length > 0) result.Add(current);
                current = null;
                continue;
            }

            switch (id)
            {
                case 0x0019: // MODULENAME
                    if (current is not null && current.Name.Length > 0) result.Add(current);
                    position = SkipSized(dir, position, out var nameBytes);
                    current = new ModuleStub(DecodeText(nameBytes, 1252), string.Empty, 0, false);
                    break;

                case 0x001A: // MODULESTREAMNAME + reserved + unicode variant
                    position = SkipSized(dir, position, out var streamBytes);
                    if (current is not null)
                        current = current with { StreamName = DecodeText(streamBytes, 1252) };
                    if (position + 2 <= dir.Length) position += 2; // reserved == 0x0032
                    position = SkipSized(dir, position, out var unicodeBytes);
                    if (current is not null && current.StreamName.Length == 0 && unicodeBytes.Length > 0)
                        current = current with { StreamName = DecodeText(unicodeBytes, 1200) };
                    break;

                case 0x0047: // MODULENAMEUNICODE
                    position = SkipSized(dir, position, out _);
                    break;

                case 0x001C: // MODULEDOCSTRING + reserved + unicode variant
                    position = SkipSized(dir, position, out _);
                    if (position + 2 <= dir.Length) position += 2;
                    position = SkipSized(dir, position, out _);
                    break;

                case 0x0031: // MODULEOFFSET
                    if (position + 4 <= dir.Length)
                    {
                        if (current is not null)
                            current = current with { Offset = (int)ReadU32(dir, position) };
                        position += 4;
                    }
                    break;

                case 0x0021: // procedural module
                case 0x0022: // document, class or designer module
                    if (current is not null)
                        current = current with { IsDocument = id == 0x0022 };
                    if (position + 4 <= dir.Length) position += 4;
                    break;

                case 0x001E: // MODULEHELPCONTEXT
                    if (position + 4 <= dir.Length) position += 4;
                    break;

                case 0x002C: // MODULECOOKIE
                    if (position + 2 <= dir.Length) position += 2;
                    break;

                case 0x0025: // MODULEREADONLY
                case 0x0028: // MODULEPRIVATE
                    break;

                default:
                    // Unknown record: skip a plausible size-prefixed payload,
                    // otherwise stop rather than walking into arbitrary bytes.
                    if (position + 4 > dir.Length) position = dir.Length;
                    else
                    {
                        var size = ReadU32(dir, position);
                        if (size <= 1024 && position + 4 + (int)size <= dir.Length)
                            position += 4 + (int)size;
                        else position = dir.Length;
                    }
                    break;
            }
        }
        if (current is not null && current.Name.Length > 0) result.Add(current);
        return result;
    }

    private static int SkipSized(byte[] dir, int position, out byte[] data)
    {
        data = Array.Empty<byte>();
        if (position + 4 > dir.Length) return dir.Length;
        var size = ReadU32(dir, position);
        position += 4;
        if (size > 1024 || position + (int)size > dir.Length) return position;
        data = dir.AsSpan(position, (int)size).ToArray();
        return position + (int)size;
    }

    private static string DecodeText(byte[] bytes, int codePage)
    {
        if (bytes.Length == 0) return string.Empty;
        if (codePage == 65001) return Encoding.UTF8.GetString(bytes);
        if (codePage is 1200 or 1201) return Encoding.Unicode.GetString(bytes);
        try { return Encoding.GetEncoding(codePage).GetString(bytes); }
        catch { return Encoding.Latin1.GetString(bytes); }
    }

    private static ushort ReadU16(byte[] data, int offset) =>
        (ushort)(data[offset] | (data[offset + 1] << 8));

    private static uint ReadU32(byte[] data, int offset) =>
        (uint)(data[offset] | (data[offset + 1] << 8) |
               (data[offset + 2] << 16) | (data[offset + 3] << 24));

    // MacroRecording.Actions is get-only, so JSON uses a writable projection.
    private sealed class RecordingDto
    {
        public string Name { get; set; } = string.Empty;
        public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
        public List<MacroAction> Actions { get; set; } = new();
    }

    private static IReadOnlyList<MacroRecording> LoadRecordingsCore()
    {
        try
        {
            if (!File.Exists(RecordingsPath)) return Array.Empty<MacroRecording>();
            var dtos = JsonSerializer.Deserialize<List<RecordingDto>>(
                File.ReadAllText(RecordingsPath), JsonOptions);
            if (dtos is null) return Array.Empty<MacroRecording>();

            var result = new List<MacroRecording>();
            foreach (var dto in dtos)
            {
                var recording = new MacroRecording { Name = dto.Name, Created = dto.Created };
                recording.Actions.AddRange(dto.Actions);
                result.Add(recording);
            }
            return result;
        }
        catch { return Array.Empty<MacroRecording>(); }
    }

    private static void WriteRecordingsCore(IReadOnlyList<MacroRecording> items)
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            var dtos = items.Select(r => new RecordingDto
            {
                Name = r.Name,
                Created = r.Created,
                Actions = r.Actions.ToList()
            }).ToList();
            File.WriteAllText(RecordingsPath, JsonSerializer.Serialize(dtos, JsonOptions));
        }
        catch
        {
            // Storage is optional; recordings still work for the current session.
        }
    }
}
