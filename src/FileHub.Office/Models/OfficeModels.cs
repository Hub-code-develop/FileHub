using System.ComponentModel;

namespace FileHub.Office.Models;

/// <summary>Supported Office document families.</summary>
public enum OfficeDocumentKind
{
    Unknown,
    Word,
    Excel,
    PowerPoint
}

public enum WordAlignment
{
    Left,
    Center,
    Right,
    Justify
}

public enum WordListKind
{
    None,
    Bullet,
    Number
}

/// <summary>A formatted span of text inside a Word paragraph.</summary>
public sealed class WordRun
{
    public string Text { get; set; } = string.Empty;
    public bool IsBold { get; set; }
    public bool IsItalic { get; set; }
    public bool IsUnderline { get; set; }
    public string FontName { get; set; } = string.Empty;
    public double FontSize { get; set; }          // points; 0 = inherit from style
    public string ColorHex { get; set; } = string.Empty; // RRGGBB without '#'
    public string Hyperlink { get; set; } = string.Empty;

    public WordRun Clone() => new()
    {
        Text = Text,
        IsBold = IsBold,
        IsItalic = IsItalic,
        IsUnderline = IsUnderline,
        FontName = FontName,
        FontSize = FontSize,
        ColorHex = ColorHex,
        Hyperlink = Hyperlink
    };
}

/// <summary>A base type for blocks that appear in the Word body in document order.</summary>
public abstract class WordBlock
{
}

/// <summary>One editable Word paragraph with run-level formatting and paragraph properties.</summary>
public sealed class WordParagraph : WordBlock, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public void NotifyChanged(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    public List<WordRun> Runs { get; } = new();

    public string Style { get; set; } = "Normal";
    public WordAlignment Alignment { get; set; } = WordAlignment.Left;
    public WordListKind ListKind { get; set; } = WordListKind.None;
    public int ListLevel { get; set; }
    public double LeftIndent { get; set; }       // points
    public double SpaceBefore { get; set; }      // points
    public double SpaceAfter { get; set; }       // points

    private bool _isEditing;
    /// <summary>UI-only flag: true while the plain-text editor has focus.</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set { if (_isEditing == value) return; _isEditing = value; NotifyChanged(nameof(IsEditing)); }
    }

    /// <summary>Concatenated run text. Assigning it rebuilds runs while keeping the leading format.</summary>
    public string PlainText
    {
        get => string.Concat(Runs.Select(r => r.Text));
        set
        {
            var leader = Runs.FirstOrDefault()?.Clone() ?? new WordRun();
            Runs.Clear();
            if (!string.IsNullOrEmpty(value))
            {
                leader.Text = value;
                Runs.Add(leader);
            }
        }
    }

    public string LeadingFontName => Runs.FirstOrDefault()?.FontName ?? string.Empty;
    public double LeadingFontSize => Runs.FirstOrDefault()?.FontSize ?? 0;
    public string LeadingColor => Runs.FirstOrDefault()?.ColorHex ?? string.Empty;

    public string DisplayText
    {
        get
        {
            var text = PlainText;
            if (ListKind == WordListKind.None) return text;
            var indent = new string(' ', Math.Max(0, ListLevel) * 2);
            return ListKind == WordListKind.Bullet
                ? $"{indent}•  {text}"
                : $"{indent}{ListLevel + 1}.  {text}";
        }
    }

    public WordParagraph Clone()
    {
        var copy = new WordParagraph
        {
            Style = Style,
            Alignment = Alignment,
            ListKind = ListKind,
            ListLevel = ListLevel,
            LeftIndent = LeftIndent,
            SpaceBefore = SpaceBefore,
            SpaceAfter = SpaceAfter
        };
        foreach (var run in Runs) copy.Runs.Add(run.Clone());
        return copy;
    }
}

/// <summary>A simple Word table. Cells are edited as plain text; paragraph formatting is not kept.</summary>
public sealed class WordTableCell : INotifyPropertyChanged
{
    private string _text = string.Empty;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Text { get => _text; set { if (_text == value) return; _text = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text))); } }
}

public sealed class WordTable : WordBlock
{
    public List<List<WordTableCell>> Rows { get; } = new();
    public int ColumnCount { get; set; } = 3;

    public static WordTable Create(int rows, int columns)
    {
        var table = new WordTable { ColumnCount = Math.Max(1, columns) };
        for (var r = 0; r < Math.Max(1, rows); r++)
            table.Rows.Add(Enumerable.Range(0, table.ColumnCount).Select(_ => new WordTableCell()).ToList());
        return table;
    }
}

/// <summary>An inline picture block in a Word document.</summary>
public sealed class WordImage : WordBlock
{
    public string Path { get; set; } = string.Empty;
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public string ContentType { get; set; } = "image/png";
    public int WidthPx { get; set; } = 480;
    public int HeightPx { get; set; } = 270;
}

/// <summary>A worksheet cell. Formula starts with '='; Display is the cached/evaluated value.</summary>
public sealed class ExcelCell : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public void NotifyChanged(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    public string Reference { get; set; } = string.Empty;
    public int Row { get; set; }
    public int Column { get; set; }
    public string Display { get; set; } = string.Empty;
    public string Formula { get; set; } = string.Empty;
    public string NumberFormat { get; set; } = string.Empty;

    public bool IsBold { get; set; }
    public bool IsItalic { get; set; }
    public bool IsUnderline { get; set; }
    public string FontColor { get; set; } = string.Empty;
    public string FillColor { get; set; } = string.Empty;
    public WordAlignment Alignment { get; set; } = WordAlignment.Left;

    public bool HasFormula => Formula.StartsWith('=');
    public string EditingText
    {
        get => HasFormula ? Formula : Display;
        set
        {
            if (value.StartsWith('=')) Formula = value;
            else { Formula = string.Empty; Display = value; }
            NotifyChanged(nameof(EditingText));
            NotifyChanged(nameof(Display));
        }
    }

    public ExcelCell Clone() => new()
    {
        Reference = Reference, Row = Row, Column = Column, Display = Display, Formula = Formula,
        NumberFormat = NumberFormat, IsBold = IsBold, IsItalic = IsItalic,
        IsUnderline = IsUnderline, FontColor = FontColor, FillColor = FillColor,
        Alignment = Alignment
    };
}

/// <summary>One worksheet with a rectangular cell grid.</summary>
public sealed class ExcelSheet
{
    public string Name { get; set; } = string.Empty;
    public List<ExcelCell> Cells { get; } = new();
    public List<double> ColumnWidths { get; } = new();
    public int RowCount { get; set; } = 60;
    public int ColumnCount { get; set; } = 20;

    public ExcelCell? Get(int row, int column) =>
        Cells.FirstOrDefault(c => c.Row == row && c.Column == column);

    public ExcelCell Ensure(int row, int column)
    {
        var existing = Get(row, column);
        if (existing is not null) return existing;
        var cell = new ExcelCell
        {
            Row = row,
            Column = column,
            Reference = CellReference(row, column)
        };
        Cells.Add(cell);
        return cell;
    }

    public static string CellReference(int row, int column)
    {
        var text = string.Empty;
        while (column > 0)
        {
            var rem = (column - 1) % 26;
            text = (char)('A' + rem) + text;
            column = (column - 1) / 26;
        }
        return text + row;
    }

    public static (int Row, int Column) ParseReference(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return (0, 0);
        var col = 0;
        var i = 0;
        while (i < reference.Length && char.IsLetter(reference[i]))
        {
            col = col * 26 + (char.ToUpperInvariant(reference[i]) - 'A' + 1);
            i++;
        }
        if (!int.TryParse(reference.AsSpan(i), out var row)) return (0, 0);
        return (row, col);
    }
}

/// <summary>An editable shape on a slide (title, body or text box).</summary>
public sealed class PowerPointShape : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public void NotifyChanged(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    public string Name { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool IsTitle { get; set; }
    public double FontSize { get; set; } = 18;
    public bool IsBold { get; set; }
    public string ColorHex { get; set; } = string.Empty;

    public PowerPointShape Clone() => new()
    {
        Name = Name, Text = Text, X = X, Y = Y, Width = Width, Height = Height,
        IsTitle = IsTitle, FontSize = FontSize, IsBold = IsBold, ColorHex = ColorHex
    };
}

/// <summary>One editable slide.</summary>
public sealed class PowerPointSlide
{
    public int Number { get; set; }
    public string Layout { get; set; } = "Title and Content";
    public string Notes { get; set; } = string.Empty;
    public List<PowerPointShape> Shapes { get; } = new();

    public string Title => Shapes.FirstOrDefault(s => s.IsTitle)?.Text ?? string.Empty;
    public IEnumerable<string> Lines => Shapes.Where(s => !s.IsTitle && !string.IsNullOrWhiteSpace(s.Text)).Select(s => s.Text);

    public PowerPointSlide Clone()
    {
        var copy = new PowerPointSlide { Number = Number, Layout = Layout, Notes = Notes };
        foreach (var shape in Shapes) copy.Shapes.Add(shape.Clone());
        return copy;
    }
}

/// <summary>Normalised representation of any supported document.</summary>
public sealed class OfficeDocument
{
    public string Path { get; set; } = string.Empty;
    public OfficeDocumentKind Kind { get; set; }
    public string FileName => System.IO.Path.GetFileName(Path);

    // Word
    public List<WordBlock> Blocks { get; } = new();
    public IEnumerable<WordParagraph> Paragraphs => Blocks.OfType<WordParagraph>();

    // Excel
    public List<ExcelSheet> Sheets { get; } = new();

    // PowerPoint
    public List<PowerPointSlide> Slides { get; } = new();
}
