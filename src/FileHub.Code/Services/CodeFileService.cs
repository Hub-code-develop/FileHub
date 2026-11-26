using System.Text;

namespace FileHub.Code.Services;

/// <summary>Text I/O for the code editor. Detects the BOM so saving keeps the original encoding.</summary>
public static class CodeFileService
{
    /// <summary>Refuse to load very large files into a web editor.</summary>
    public const long MaxFileBytes = 16L * 1024 * 1024;

    public sealed record LoadedText(string Text, string EncodingName);

    public static LoadedText Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException(path);
        if (info.Length > MaxFileBytes)
            throw new IOException($"File is larger than {MaxFileBytes / 1024 / 1024} MB and cannot be opened in the editor.");

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new LoadedText(new UTF8Encoding(false, false).GetString(bytes, 3, bytes.Length - 3), "UTF-8 with BOM");
        // UTF-32 LE starts with the UTF-16 LE BOM, so it must be checked first.
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0)
            return new LoadedText(Encoding.UTF32.GetString(bytes, 4, bytes.Length - 4), "UTF-32 LE");
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return new LoadedText(Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16 LE");
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return new LoadedText(Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16 BE");

        // Lenient UTF-8: a single bad byte must not make an otherwise valid source file unopenable.
        return new LoadedText(new UTF8Encoding(false, false).GetString(bytes), "UTF-8");
    }

    public static void Write(string path, string text, string encodingName)
    {
        byte[] bytes;
        switch (encodingName)
        {
            case "UTF-8 with BOM":
                bytes = Encoding.UTF8.GetPreamble()
                    .Concat(new UTF8Encoding(false, false).GetBytes(text)).ToArray();
                break;
            case "UTF-16 LE":
                bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray();
                break;
            case "UTF-16 BE":
                bytes = Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(text)).ToArray();
                break;
            case "UTF-32 LE":
                bytes = Encoding.UTF32.GetPreamble().Concat(Encoding.UTF32.GetBytes(text)).ToArray();
                break;
            default:
                bytes = new UTF8Encoding(false, false).GetBytes(text);
                break;
        }
        File.WriteAllBytes(path, bytes);
    }
}
