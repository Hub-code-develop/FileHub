namespace FileHub.Office.Models;

/// <summary>Supported Office document families.</summary>
public enum OfficeDocumentKind
{
    Unknown,
    Word,
    Excel,
    PowerPoint
}

/// <summary>One editable paragraph in a Word document.</summary>
public sealed class WordParagraph
{
    public string Text { get; set; } = string.Empty;
    public string Style { get; set; } = "Normal";
    public bool IsBold { get; set; }
    public bool IsItalic { get; set; }
    public bool IsUnderline { get; set; }
}

/// <summary>A worksheet rendered as a grid of text cells.</summary>
public sealed class ExcelSheet
{
    public string Name { get; set; } = string.Empty;
    public List<ExcelCell> Cells { get; } = new();
}

public sealed class ExcelCell
{
    public string Reference { get; set; } = string.Empty;   // A1, B2, ...
    public int Row { get; set; }
    public int Column { get; set; }
    public string Text { get; set; } = string.Empty;
}

/// <summary>One slide reduced to its title and body lines (the outline shown in the editor).</summary>
public sealed class PowerPointSlide
{
    public int Number { get; set; }
    public string Title { get; set; } = string.Empty;
    public List<string> Lines { get; } = new();
}

/// <summary>Normalised representation of any supported document.</summary>
public sealed class OfficeDocument
{
    public string Path { get; set; } = string.Empty;
    public OfficeDocumentKind Kind { get; set; }
    public string FileName => System.IO.Path.GetFileName(Path);
    public List<WordParagraph> Paragraphs { get; } = new();
    public List<ExcelSheet> Sheets { get; } = new();
    public List<PowerPointSlide> Slides { get; } = new();
}
