using System.Text;
using FileHub.Office.Models;

namespace FileHub.Office.Services;

/// <summary>Shared document helpers used by the editor view model.</summary>
public static class OfficeTools
{
    public static OfficeDocument Clone(OfficeDocument source)
    {
        var copy = new OfficeDocument { Path = source.Path, Kind = source.Kind };
        foreach (var block in source.Blocks)
        {
            switch (block)
            {
                case WordParagraph paragraph:
                    copy.Blocks.Add(paragraph.Clone());
                    break;
                case WordTable table:
                    var tableCopy = new WordTable { ColumnCount = table.ColumnCount };
                    foreach (var row in table.Rows) tableCopy.Rows.Add(row.Select(c => new WordTableCell { Text = c.Text }).ToList());
                    copy.Blocks.Add(tableCopy);
                    break;
                case WordImage image:
                    copy.Blocks.Add(new WordImage
                    {
                        Path = image.Path, Data = image.Data, ContentType = image.ContentType,
                        WidthPx = image.WidthPx, HeightPx = image.HeightPx
                    });
                    break;
            }
        }
        foreach (var sheet in source.Sheets)
        {
            var sheetCopy = new ExcelSheet
            {
                Name = sheet.Name, RowCount = sheet.RowCount, ColumnCount = sheet.ColumnCount
            };
            sheetCopy.ColumnWidths.AddRange(sheet.ColumnWidths);
            foreach (var cell in sheet.Cells) sheetCopy.Cells.Add(cell.Clone());
            copy.Sheets.Add(sheetCopy);
        }
        foreach (var slide in source.Slides) copy.Slides.Add(slide.Clone());

        foreach (var definition in source.NumberingDefinitions)
        {
            var clone = new NumberingDefinition
            {
                NumberId = definition.NumberId, AbstractId = definition.AbstractId,
                Name = definition.Name, IsLegal = definition.IsLegal
            };
            foreach (var level in definition.Levels)
                clone.Levels.Add(new NumberingLevel
                {
                    Index = level.Index, Format = level.Format, Text = level.Text,
                    Start = level.Start, RestartAfterLevel = level.RestartAfterLevel,
                    LeftIndent = level.LeftIndent, HangingIndent = level.HangingIndent,
                    BulletChar = level.BulletChar, FontName = level.FontName
                });
            copy.NumberingDefinitions.Add(clone);
        }
        foreach (var revision in source.Revisions) copy.Revisions.Add(revision.Clone());
        foreach (var pair in source.NumberingCounters)
            copy.NumberingCounters[pair.Key] = (int[])pair.Value.Clone();
        copy.TrackChangesEnabled = source.TrackChangesEnabled;
        copy.OriginalParagraphs.AddRange(source.OriginalParagraphs);
        foreach (var chart in source.Charts) copy.Charts.Add(chart);
        foreach (var pivot in source.PivotTables) copy.PivotTables.Add(pivot);
        foreach (var embedded in source.EmbeddedObjects) copy.EmbeddedObjects.Add(embedded);
        copy.MacroProject.HasVbaProject = source.MacroProject.HasVbaProject;
        copy.MacroProject.ProjectName = source.MacroProject.ProjectName;
        copy.MacroProject.Version = source.MacroProject.Version;
        copy.MacroProject.Warning = source.MacroProject.Warning;
        foreach (var module in source.MacroProject.Modules) copy.MacroProject.Modules.Add(module);
        copy.PreserveVbaProject = source.PreserveVbaProject;
        return copy;
    }

    public static (int Pages, int Words, int Characters) WordStatistics(OfficeDocument doc)
    {
        var text = string.Join("\n", doc.Paragraphs.Select(p => p.PlainText));
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var characters = text.Replace("\n", string.Empty).Length;
        var paragraphs = doc.Paragraphs.Count();
        return (Math.Max(1, (paragraphs + 24) / 25), words, characters);
    }

    public static int ReplaceInWord(OfficeDocument doc, string search, string replacement, bool matchCase)
    {
        if (string.IsNullOrEmpty(search)) return 0;
        var count = 0;
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        foreach (var paragraph in doc.Paragraphs)
        foreach (var run in paragraph.Runs)
        {
            var text = run.Text;
            var index = 0;
            while ((index = text.IndexOf(search, index, comparison)) >= 0)
            {
                text = text.Remove(index, search.Length).Insert(index, replacement);
                index += replacement.Length;
                count++;
            }
            run.Text = text;
        }
        return count;
    }

    public static int FindInWord(OfficeDocument doc, string search, bool matchCase)
    {
        if (string.IsNullOrEmpty(search)) return 0;
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return doc.Paragraphs.Sum(p => p.PlainText.CountOccurrences(search, comparison));
    }

    private static int CountOccurrences(this string text, string search, StringComparison comparison)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(search, index, comparison)) >= 0)
        {
            count++;
            index += search.Length;
        }
        return count;
    }

    /// <summary>Export a lightweight, printable HTML representation of the current document.</summary>
    public static string ExportHtml(OfficeDocument doc)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\">");
        builder.AppendLine($"<title>{Escape(System.IO.Path.GetFileName(doc.Path))}</title>");
        builder.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:48px;max-width:900px;color:#222}");
        builder.AppendLine("table{border-collapse:collapse;width:100%}td,th{border:1px solid #bbb;padding:6px}");
        builder.AppendLine("h1,h2,h3{color:#2B579A}img{max-width:100%}</style></head><body>");
        switch (doc.Kind)
        {
            case OfficeDocumentKind.Word:
                foreach (var block in doc.Blocks)
                {
                    switch (block)
                    {
                        case WordParagraph paragraph:
                            var tag = paragraph.Style switch
                            {
                                "Title" => "h1",
                                "Heading1" => "h1",
                                "Heading2" => "h2",
                                "Heading3" => "h3",
                                _ => "p"
                            };
                            var align = paragraph.Alignment switch
                            {
                                WordAlignment.Center => "text-align:center;",
                                WordAlignment.Right => "text-align:right;",
                                WordAlignment.Justify => "text-align:justify;",
                                _ => string.Empty
                            };
                            var text = new StringBuilder();
                            foreach (var run in paragraph.Runs)
                            {
                                var span = new StringBuilder(Escape(run.Text).Replace("\n", "<br>"));
                                if (run.IsBold) span.Insert(0, "<strong>").Append("</strong>");
                                if (run.IsItalic) span.Insert(0, "<em>").Append("</em>");
                                if (run.IsUnderline) span.Insert(0, "<u>").Append("</u>");
                                if (!string.IsNullOrEmpty(run.Hyperlink))
                                    span.Insert(0, $"<a href=\"{Escape(run.Hyperlink)}\">").Append("</a>");
                                text.Append(span);
                            }
                            builder.AppendLine($"<{tag} style=\"{align}\">{text}</{tag}>");
                            break;
                        case WordTable table:
                            builder.AppendLine("<table>");
                            foreach (var row in table.Rows)
                            {
                                builder.AppendLine("<tr>");
                                foreach (var cell in row) builder.Append($"<td>{Escape(cell.Text)}</td>");
                                builder.AppendLine("</tr>");
                            }
                            builder.AppendLine("</table>");
                            break;
                        case WordImage image:
                            var base64 = Convert.ToBase64String(image.Data);
                            var mime = string.IsNullOrEmpty(image.ContentType) ? "image/png" : image.ContentType;
                            builder.AppendLine($"<p><img src=\"data:{mime};base64,{base64}\"></p>");
                            break;
                    }
                }
                break;
            case OfficeDocumentKind.Excel:
                foreach (var sheet in doc.Sheets)
                {
                    builder.AppendLine($"<h2>{Escape(sheet.Name)}</h2><table>");
                    var maxRow = sheet.Cells.Count == 0 ? 0 : sheet.Cells.Max(c => c.Row);
                    var maxCol = sheet.Cells.Count == 0 ? 0 : sheet.Cells.Max(c => c.Column);
                    for (var r = 1; r <= Math.Min(maxRow, 200); r++)
                    {
                        builder.AppendLine("<tr>");
                        for (var c = 1; c <= Math.Min(maxCol, 40); c++)
                            builder.Append($"<td>{Escape(sheet.Get(r, c)?.Display ?? string.Empty)}</td>");
                        builder.AppendLine("</tr>");
                    }
                    builder.AppendLine("</table>");
                }
                break;
            case OfficeDocumentKind.PowerPoint:
                foreach (var slide in doc.Slides)
                {
                    builder.AppendLine($"<h1>Slide {slide.Number}: {Escape(slide.Title)}</h1>");
                    foreach (var line in slide.Lines) builder.AppendLine($"<p>{Escape(line)}</p>");
                }
                break;
        }
        builder.AppendLine("</body></html>");
        return builder.ToString();
    }

    private static string Escape(string? text) => System.Net.WebUtility.HtmlEncode(text ?? string.Empty);
}
