using DocumentFormat.OpenXml.Packaging;
using FileHub.Office.Models;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace FileHub.Office.Services;

/// <summary>
/// Reads and writes Word numbering.xml: abstract definitions, instances, level formats,
/// start values, restart rules, indents and legal numbering. Also resolves the visible
/// multi-level label for every numbered paragraph.
/// </summary>
public static class WordNumberingService
{
    /// <summary>Loads every numbering instance into the document model.</summary>
    public static void Read(OfficeDocument doc, MainDocumentPart main)
    {
        var numbering = main.NumberingDefinitionsPart?.Numbering;
        if (numbering is null) return;

        var abstracts = numbering.Elements<W.AbstractNum>()
            .ToDictionary(a => a.AbstractNumberId?.Value ?? 0, a => a);

        foreach (var instance in numbering.Elements<W.NumberingInstance>())
        {
            var definition = new NumberingDefinition
            {
                NumberId = instance.NumberID?.Value ?? 0,
                AbstractId = instance.AbstractNumId?.Val?.Value ?? 0
            };
            if (definition.NumberId <= 0) continue;

            if (abstracts.TryGetValue(definition.AbstractId, out var abstractNum))
            {
                definition.IsLegal = abstractNum.Elements<W.Level>()
                    .Any(l => l.GetFirstChild<W.IsLegalNumberingStyle>() is not null);
                var name = abstractNum.GetFirstChild<W.AbstractNumDefinitionName>();
                if (name is not null)
                    definition.Name = name.GetAttribute("val", name.NamespaceUri).Value ?? string.Empty;

                foreach (var level in abstractNum.Elements<W.Level>())
                {
                    var model = ReadLevel(level);
                    definition.Levels.Add(model);
                }
            }

            foreach (var level in instance.Elements<W.LevelOverride>())
            {
                var model = definition.Levels.FirstOrDefault(l => l.Index == level.LevelIndex?.Value);
                var start = level.StartOverrideNumberingValue?.Val?.Value;
                if (model is not null && start is { } value) model.Start = value;
                if (model is not null && level.Level is { } replacement)
                {
                    var replaced = ReadLevel(replacement);
                    model.Format = replaced.Format;
                    model.Text = replaced.Text;
                    model.BulletChar = replaced.BulletChar;
                }
            }

            if (definition.Levels.Count == 0)
            {
                for (var i = 0; i < 9; i++)
                    definition.Levels.Add(CreateDefaultLevel(i, definition.IsLegal));
            }
            doc.NumberingDefinitions.Add(definition);
        }
    }

    private static NumberingLevel ReadLevel(W.Level level)
    {
        var format = level.NumberingFormat?.Val?.Value;
        var model = new NumberingLevel
        {
            Index = level.LevelIndex?.Value ?? 0,
            Format = ConvertFormat(format),
            Start = level.StartNumberingValue?.Val?.Value ?? 1,
            RestartAfterLevel = level.LevelRestart?.Val?.Value
        };
        var text = level.LevelText?.Val?.Value ?? string.Empty;
        model.Text = text.Length == 0 ? "%1." : text;
        if (model.Format == WordNumberFormat.Bullet && text.Length > 0)
            model.BulletChar = text;
        var indent = level.PreviousParagraphProperties?.Indentation;
        if (indent?.Left?.Value is { } left && int.TryParse(left, out var leftPoints))
            model.LeftIndent = leftPoints / 20.0;
        if (indent?.Hanging?.Value is { } hanging && int.TryParse(hanging, out var hangPoints))
            model.HangingIndent = hangPoints / 20.0;
        var font = level.NumberingSymbolRunProperties?.RunFonts?.Ascii?.Value;
        if (!string.IsNullOrEmpty(font)) model.FontName = font;
        return model;
    }

    private static WordNumberFormat ConvertFormat(W.NumberFormatValues? format)
    {
        if (format == W.NumberFormatValues.DecimalZero) return WordNumberFormat.DecimalZero;
        if (format == W.NumberFormatValues.LowerLetter) return WordNumberFormat.LowerLetter;
        if (format == W.NumberFormatValues.UpperLetter) return WordNumberFormat.UpperLetter;
        if (format == W.NumberFormatValues.LowerRoman) return WordNumberFormat.LowerRoman;
        if (format == W.NumberFormatValues.UpperRoman) return WordNumberFormat.UpperRoman;
        if (format == W.NumberFormatValues.Bullet) return WordNumberFormat.Bullet;
        if (format == W.NumberFormatValues.Ordinal) return WordNumberFormat.Ordinal;
        if (format == W.NumberFormatValues.None) return WordNumberFormat.None;
        return WordNumberFormat.Decimal;
    }

    private static W.NumberFormatValues ConvertFormat(WordNumberFormat format)
    {
        if (format == WordNumberFormat.DecimalZero) return W.NumberFormatValues.DecimalZero;
        if (format == WordNumberFormat.LowerLetter) return W.NumberFormatValues.LowerLetter;
        if (format == WordNumberFormat.UpperLetter) return W.NumberFormatValues.UpperLetter;
        if (format == WordNumberFormat.LowerRoman) return W.NumberFormatValues.LowerRoman;
        if (format == WordNumberFormat.UpperRoman) return W.NumberFormatValues.UpperRoman;
        if (format == WordNumberFormat.Bullet) return W.NumberFormatValues.Bullet;
        if (format == WordNumberFormat.Ordinal) return W.NumberFormatValues.Ordinal;
        if (format == WordNumberFormat.None) return W.NumberFormatValues.None;
        return W.NumberFormatValues.Decimal;
    }

    private static NumberingLevel CreateDefaultLevel(int index, bool legal)
    {
        var level = new NumberingLevel
        {
            Index = index,
            Format = legal && index > 0 ? WordNumberFormat.Decimal : WordNumberFormat.Decimal,
            Text = index == 0 ? "%1." : $"%{index + 1}.",
            LeftIndent = 36 * (index + 1),
            HangingIndent = 18
        };
        return level;
    }

    /// <summary>Ensures the two default instances used by the editor exist.</summary>
    public static void EnsureDefaults(OfficeDocument doc)
    {
        if (!doc.NumberingDefinitions.Any(d => d.NumberId == 1))
        {
        var bullets = new NumberingDefinition { NumberId = 1, AbstractId = 1, Name = "FileHub bullets" };
        for (var i = 0; i < 9; i++)
        {
            var level = CreateDefaultLevel(i, false);
            level.Format = WordNumberFormat.Bullet;
            level.Text = i switch { 1 => "\u25e6", 2 => "\u25aa", _ => "\u2022" };
            level.BulletChar = level.Text;
            bullets.Levels.Add(level);
        }
        doc.NumberingDefinitions.Add(bullets);
        }

        if (!doc.NumberingDefinitions.Any(d => d.NumberId == 2))
        {
            var numbers = new NumberingDefinition { NumberId = 2, AbstractId = 2, Name = "FileHub multilevel" };
            for (var i = 0; i < 9; i++)
                numbers.Levels.Add(CreateDefaultLevel(i, false));
            doc.NumberingDefinitions.Add(numbers);
        }
    }

    /// <summary>Writes all definitions back to numbering.xml, keeping parts and relationships intact.</summary>
    public static void Write(OfficeDocument doc, MainDocumentPart main)
    {
        EnsureDefaults(doc);
        var part = main.NumberingDefinitionsPart ?? main.AddNewPart<NumberingDefinitionsPart>();
        W.Numbering numbering;
        if (part.Numbering is { } existing)
        {
            numbering = existing;
            numbering.RemoveAllChildren();
        }
        else
        {
            numbering = new W.Numbering();
            part.Numbering = numbering;
        }

        foreach (var definition in doc.NumberingDefinitions)
        {
            var abstractId = definition.AbstractId > 0
                ? definition.AbstractId
                : doc.NumberingDefinitions.IndexOf(definition) + 1;
            var abstractNum = new W.AbstractNum
            {
                AbstractNumberId = abstractId,
                MultiLevelType = new W.MultiLevelType { Val = W.MultiLevelValues.Multilevel }
            };
            foreach (var model in definition.Levels.OrderBy(l => l.Index))
            {
                var level = new W.Level
                {
                    LevelIndex = model.Index,
                    StartNumberingValue = new W.StartNumberingValue { Val = Math.Max(0, model.Start) },
                    NumberingFormat = new W.NumberingFormat { Val = ConvertFormat(model.Format) }
                };
                if (model.RestartAfterLevel is { } restart)
                    level.AppendChild(new W.LevelRestart { Val = restart });
                if (definition.IsLegal)
                    level.AppendChild(new W.IsLegalNumberingStyle());
                level.AppendChild(new W.LevelText { Val = model.Text });
                level.AppendChild(new W.PreviousParagraphProperties(
                    new W.Indentation
                    {
                        Left = ((int)Math.Round(model.LeftIndent * 20)).ToString(),
                        Hanging = ((int)Math.Round(model.HangingIndent * 20)).ToString()
                    }));
                if (!string.IsNullOrEmpty(model.FontName))
                    level.AppendChild(new W.NumberingSymbolRunProperties(
                        new W.RunFonts { Ascii = model.FontName, HighAnsi = model.FontName }));
                abstractNum.AppendChild(level);
            }
            numbering.AppendChild(abstractNum);

            var instance = new W.NumberingInstance(
                new W.AbstractNumId { Val = abstractId })
            {
                NumberID = definition.NumberId
            };
            numbering.AppendChild(instance);
        }

        numbering.Save();
    }

    /// <summary>Recomputes the visible labels of all numbered paragraphs.</summary>
    public static void RefreshLabels(OfficeDocument doc)
    {
        doc.NumberingCounters.Clear();
        foreach (var paragraph in doc.Paragraphs)
        {
            if (paragraph.ListKind == WordListKind.None)
            {
                paragraph.NumberLabel = string.Empty;
                continue;
            }
            var definition = doc.NumberingDefinitions.FirstOrDefault(d => d.NumberId == paragraph.NumberId)
                             ?? doc.NumberingDefinitions.FirstOrDefault(d => d.NumberId == (paragraph.ListKind == WordListKind.Bullet ? 1 : 2));
            if (definition is null)
            {
                paragraph.NumberLabel = paragraph.ListKind == WordListKind.Bullet ? "\u2022" : $"{paragraph.ListLevel + 1}.";
                continue;
            }
            if (!doc.NumberingCounters.TryGetValue(definition.NumberId, out var counters))
            {
                counters = new int[9];
                doc.NumberingCounters[definition.NumberId] = counters;
            }
            var level = Math.Clamp(paragraph.ListLevel, 0, 8);
            var rule = definition.Level(level);
            var start = rule?.Start ?? 1;
            counters[level] = counters[level] == 0 ? start : counters[level] + 1;
            for (var deeper = level + 1; deeper < counters.Length; deeper++)
                counters[deeper] = 0;

            if (rule?.RestartAfterLevel is { } after && after >= 0 && after < level)
            {
                for (var deeper = after + 1; deeper < counters.Length; deeper++)
                    counters[deeper] = deeper == level ? counters[level] : 0;
            }
            paragraph.NumberLabel = definition.Label(level, counters);
        }
    }

    /// <summary>Applies a numbering definition/level to a paragraph and refreshes labels.</summary>
    public static void Apply(OfficeDocument doc, WordParagraph paragraph, int numberId, int level)
    {
        var definition = doc.NumberingDefinitions.FirstOrDefault(d => d.NumberId == numberId);
        paragraph.NumberId = numberId;
        paragraph.ListLevel = Math.Clamp(level, 0, 8);
        paragraph.ListKind = definition?.Level(paragraph.ListLevel)?.Format == WordNumberFormat.Bullet
            ? WordListKind.Bullet
            : WordListKind.Number;
        RefreshLabels(doc);
    }
}
