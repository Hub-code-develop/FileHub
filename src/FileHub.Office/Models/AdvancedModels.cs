using System.ComponentModel;

namespace FileHub.Office.Models;

// ------------------------------------------------------------------ numbering

/// <summary>Numbering formats supported by Word's numbering.xml.</summary>
public enum WordNumberFormat
{
    None,
    Decimal,
    LowerLetter,
    UpperLetter,
    LowerRoman,
    UpperRoman,
    Bullet,
    Ordinal,
    DecimalZero
}

/// <summary>One level of a multi-level numbering definition.</summary>
public sealed class NumberingLevel
{
    public int Index { get; set; }
    public WordNumberFormat Format { get; set; } = WordNumberFormat.Decimal;
    public string Text { get; set; } = "%1.";
    public int Start { get; set; } = 1;
    public int? RestartAfterLevel { get; set; }
    public double LeftIndent { get; set; } = 36;
    public double HangingIndent { get; set; } = 18;
    public string BulletChar { get; set; } = "\u2022";
    public string FontName { get; set; } = string.Empty;
}

/// <summary>A numbering instance together with its abstract definition.</summary>
public sealed class NumberingDefinition
{
    public int NumberId { get; set; }
    public int AbstractId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsLegal { get; set; }
    public List<NumberingLevel> Levels { get; } = new();

    public NumberingLevel? Level(int index) => Levels.FirstOrDefault(l => l.Index == index);
    public string LevelsSummary => string.Join("  |  ", Levels.Take(4).Select(l => Label(l.Index, 1, 1, 1)));

    /// <summary>Formats a counter value using this level's numbering format.</summary>
    public string Label(int level, params int[] counters)
    {
        var definition = Level(level);
        if (definition is null) return string.Empty;
        var value = counters.ElementAtOrDefault(level);
        if (value <= 0) value = definition.Start;
        var token = definition.Format switch
        {
            WordNumberFormat.Decimal => value.ToString(),
            WordNumberFormat.DecimalZero => value.ToString("00"),
            WordNumberFormat.LowerLetter => ToLetters(value, upper: false),
            WordNumberFormat.UpperLetter => ToLetters(value, upper: true),
            WordNumberFormat.LowerRoman => ToRoman(value, upper: false),
            WordNumberFormat.UpperRoman => ToRoman(value, upper: true),
            WordNumberFormat.Ordinal => value + OrdinalSuffix(value),
            WordNumberFormat.Bullet => definition.BulletChar,
            _ => string.Empty
        };
        var label = definition.Text;
        for (var i = 0; i < 9; i++)
        {
            var counter = i == level ? token : counters.ElementAtOrDefault(i).ToString();
            label = label.Replace($"%{i + 1}", counter, StringComparison.Ordinal);
        }
        return label;
    }

    private static string ToLetters(int value, bool upper)
    {
        var text = string.Empty;
        while (value > 0)
        {
            value--;
            text = (char)('a' + value % 26) + text;
            value /= 26;
        }
        return upper ? text.ToUpperInvariant() : text;
    }

    private static string ToRoman(int value, bool upper)
    {
        if (value < 1 || value > 3999) return value.ToString();
        var text = string.Empty;
        foreach (var (number, roman) in new[]
                 {
                     (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"),
                     (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")
                 })
        {
            while (value >= number) { text += roman; value -= number; }
        }
        return upper ? text : text.ToLowerInvariant();
    }

    private static string OrdinalSuffix(int value)
    {
        if (value % 100 is 11 or 12 or 13) return "th";
        return (value % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
    }
}

// ------------------------------------------------------------------ revisions

public enum WordRevisionKind
{
    Insertion,
    Deletion,
    FormatChange,
    MoveFrom,
    MoveTo
}

/// <summary>One tracked change read from (or to be written to) a Word document.</summary>
public sealed class WordRevision
{
    public int Id { get; set; }
    public WordRevisionKind Kind { get; set; }
    public string Author { get; set; } = string.Empty;
    public DateTimeOffset? Date { get; set; }
    public string Text { get; set; } = string.Empty;
    public int ParagraphIndex { get; set; }
    public string FormatSummary { get; set; } = string.Empty;
    public bool IsAccepted { get; set; }
    public bool IsRejected { get; set; }

    public WordRevision Clone() => (WordRevision)MemberwiseClone();
}

// ------------------------------------------------------------------ charts

public enum OfficeChartType
{
    Column,
    Bar,
    Line,
    Pie,
    Area
}

public sealed class ChartSeries
{
    public string Name { get; set; } = string.Empty;
    public List<double> Values { get; } = new();
    public string ValueRange { get; set; } = string.Empty;
}

/// <summary>A chart anchored to a worksheet. Values are materialised so it can be rendered.</summary>
public sealed class OfficeChart
{
    public string Name { get; set; } = string.Empty;
    public OfficeChartType Type { get; set; } = OfficeChartType.Column;
    public string Title { get; set; } = string.Empty;
    public string SheetName { get; set; } = string.Empty;
    public string AnchorCell { get; set; } = "H2";
    public int WidthPx { get; set; } = 520;
    public int HeightPx { get; set; } = 320;
    public List<string> Categories { get; } = new();
    public List<ChartSeries> Series { get; } = new();
    public int PartIndex { get; set; }

    public double MaxValue => Series.SelectMany(s => s.Values).DefaultIfEmpty(0).Max();
    public double MinValue => Math.Min(0, Series.SelectMany(s => s.Values).DefaultIfEmpty(0).Min());
}

// ------------------------------------------------------------------ pivot tables

public enum PivotAggregation
{
    Sum,
    Count,
    Average,
    Min,
    Max
}

public sealed class PivotTableModel
{
    public string Name { get; set; } = string.Empty;
    public string SourceSheet { get; set; } = string.Empty;
    public string SourceRange { get; set; } = string.Empty;
    public string RowField { get; set; } = string.Empty;
    public string ColumnField { get; set; } = string.Empty;
    public string ValueField { get; set; } = string.Empty;
    public PivotAggregation Aggregation { get; set; } = PivotAggregation.Sum;
    public List<string> Fields { get; } = new();
    /// <summary>Computed grid including a header row and header column.</summary>
    public List<List<string>> Grid { get; } = new();
    public int PartIndex { get; set; }
}

// ------------------------------------------------------------------ embedded objects

/// <summary>An OLE object or embedded package stored inside an Office file.</summary>
public sealed class OfficeEmbeddedObject
{
    public string Name { get; set; } = string.Empty;
    public string ProgId { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public string PartUri { get; set; } = string.Empty;
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public byte[]? Icon { get; set; }
    public string ExtractedFileName { get; set; } = string.Empty;
    public bool IsPackage { get; set; }
    public string DocumentName { get; set; } = string.Empty;
}

// ------------------------------------------------------------------ macros

public enum MacroSecurityLevel
{
    Disabled,
    Prompt,
    Enabled
}

public sealed class MacroModule
{
    public string Name { get; set; } = string.Empty;
    public string StreamName { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public bool IsDocumentModule { get; set; }
}

public sealed class MacroProjectInfo
{
    public bool HasVbaProject { get; set; }
    public string ProjectName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public List<MacroModule> Modules { get; } = new();
    public string Warning { get; set; } = string.Empty;
}

public sealed class MacroAction
{
    public string Kind { get; set; } = string.Empty;
    public string Parameter { get; set; } = string.Empty;
}

/// <summary>A FileHub-native macro: a replayable sequence of editor actions.</summary>
public sealed class MacroRecording
{
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    public List<MacroAction> Actions { get; } = new();
}

// ------------------------------------------------------------------ document attachments

public sealed partial class OfficeDocument
{
    /// <summary>Word multi-level numbering definitions loaded from numbering.xml.</summary>
    public List<NumberingDefinition> NumberingDefinitions { get; } = new();

    /// <summary>Word tracked changes.</summary>
    public List<WordRevision> Revisions { get; } = new();

    /// <summary>Chapter numbering counters used to render list labels.</summary>
    public Dictionary<int, int[]> NumberingCounters { get; } = new();

    /// <summary>Whether new edits should be recorded as tracked changes.</summary>
    public bool TrackChangesEnabled { get; set; }

    /// <summary>Paragraph texts captured when the document was opened; used for change tracking.</summary>
    public List<string> OriginalParagraphs { get; } = new();

    /// <summary>Charts anchored to worksheets.</summary>
    public List<OfficeChart> Charts { get; } = new();

    /// <summary>Pivot tables in the workbook.</summary>
    public List<PivotTableModel> PivotTables { get; } = new();

    /// <summary>OLE packages and embedded objects.</summary>
    public List<OfficeEmbeddedObject> EmbeddedObjects { get; } = new();

    /// <summary>VBA project metadata and decompressed module sources.</summary>
    public MacroProjectInfo MacroProject { get; } = new();

    /// <summary>True when Save must retain the VBA project part.</summary>
    public bool PreserveVbaProject { get; set; }
}

// ------------------------------------------------------------------ WPS formats

/// <summary>Kingsoft WPS native document families.</summary>
public enum WpsDocumentKind
{
    Unknown,
    Writer,
    Spreadsheet,
    Presentation
}
