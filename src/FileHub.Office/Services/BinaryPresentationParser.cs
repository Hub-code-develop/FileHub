using System.Text;
using FileHub.Office.Models;

namespace FileHub.Office.Services;

/// <summary>
/// Fallback text recovery for Kingsoft WPS Presentation (.dps/.dpt) and legacy binary .ppt.
/// The binary PowerPoint format stores text in TextCharsAtom / TextBytesAtom records. This reader
/// groups recovered text into editable slides; master layouts, images and animations are not read.
/// </summary>
public static class BinaryPresentationParser
{
    private const ushort TextCharsAtom = 0x0FA0;
    private const ushort TextBytesAtom = 0x0FA8;
    private const ushort SlidePersistAtom = 0x03F3;

    static BinaryPresentationParser()
    {
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { /* optional */ }
    }

    public static List<PowerPointSlide> Parse(IReadOnlyDictionary<string, byte[]> streams, out string warning)
    {
        warning = string.Empty;
        var slides = new List<PowerPointSlide>();
        if (!streams.TryGetValue("PowerPoint Document", out var document) || document.Length < 8)
        {
            warning = "PowerPoint Document stream is missing.";
            return slides;
        }

        try
        {
            var texts = new List<string>();
            var position = 0;
            while (position + 8 <= document.Length)
            {
                var type = ReadU16(document, position + 2);
                var length = (int)ReadU32(document, position + 4);
                var payload = position + 8;
                if (length < 0 || payload + length > document.Length) break;

                if (type == TextCharsAtom && length >= 2)
                {
                    var value = Encoding.Unicode.GetString(document, payload, length & ~1).Trim();
                    if (IsUseful(value)) texts.Add(value);
                }
                else if (type == TextBytesAtom && length > 0)
                {
                    var value = GetAnsi().GetString(document, payload, length).Trim();
                    if (IsUseful(value)) texts.Add(value);
                }
                else if (type == SlidePersistAtom && texts.Count > 0 && texts.Count % 4 == 0)
                {
                    // A new slide usually starts after a persistent slide reference.
                    texts.Add("\u0000SLIDE\u0000");
                }
                position = payload + length;
            }

            var groups = new List<List<string>>();
            var current = new List<string>();
            foreach (var text in texts)
            {
                if (text == "\u0000SLIDE\u0000")
                {
                    if (current.Count > 0) groups.Add(current);
                    current = new List<string>();
                    continue;
                }
                current.Add(text);
                if (current.Count >= 4)
                {
                    groups.Add(current);
                    current = new List<string>();
                }
            }
            if (current.Count > 0) groups.Add(current);
            if (groups.Count == 0) groups.Add(new List<string>());

            for (var index = 0; index < groups.Count; index++)
            {
                var slide = new PowerPointSlide { Number = index + 1 };
                var group = groups[index];
                slide.Shapes.Add(new PowerPointShape
                {
                    Name = "Title",
                    Text = group.FirstOrDefault() ?? $"Slide {index + 1}",
                    X = 60, Y = 40, Width = 840, Height = 90,
                    IsTitle = true,
                    FontSize = 30,
                    IsBold = true
                });
                var body = string.Join("\n", group.Skip(1));
                slide.Shapes.Add(new PowerPointShape
                {
                    Name = "Body",
                    Text = body,
                    X = 60, Y = 150, Width = 840, Height = 300,
                    FontSize = 18
                });
                slides.Add(slide);
            }
        }
        catch (Exception ex)
        {
            warning = "WPS Presentation decode failed: " + ex.Message;
        }
        return slides;
    }

    private static bool IsUseful(string text) =>
        text.Length > 0 && text.Count(char.IsControl) < Math.Max(1, text.Length / 4);

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
}
