using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using FileHub.Office.Models;
using D = DocumentFormat.OpenXml.Drawing;
using S = DocumentFormat.OpenXml.Spreadsheet;
using P = DocumentFormat.OpenXml.Presentation;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace FileHub.Office.Services;

/// <summary>
/// Reads and writes docx / xlsx / pptx through the Open XML SDK. No COM, no Office install,
/// cross-platform. The model is intentionally small: this is a built-in editor for browsing and
/// light edits, not a full Word/Excel/PowerPoint clone.
/// </summary>
public static class OfficeDocumentService
{
    private const int MinGridRows = 30;
    private const int MinGridColumns = 20;

    /// <summary>Extensions handled by the built-in Office page.</summary>
    public static bool IsOfficeFile(string path)
        => Detect(path) != OfficeDocumentKind.Unknown;

    public static OfficeDocumentKind Detect(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return OfficeDocumentKind.Unknown;
        return ext.ToLowerInvariant() switch
        {
            ".docx" or ".docm" => OfficeDocumentKind.Word,
            ".xlsx" or ".xlsm" => OfficeDocumentKind.Excel,
            ".pptx" or ".pptm" => OfficeDocumentKind.PowerPoint,
            _ => OfficeDocumentKind.Unknown
        };
    }

    public static OfficeDocument Load(string path)
    {
        var kind = Detect(path);
        var doc = new OfficeDocument { Path = path, Kind = kind };
        switch (kind)
        {
            case OfficeDocumentKind.Word: LoadWord(doc); break;
            case OfficeDocumentKind.Excel: LoadExcel(doc); break;
            case OfficeDocumentKind.PowerPoint: LoadPowerPoint(doc); break;
            default: throw new NotSupportedException($"Unsupported Office file: {path}");
        }
        return doc;
    }

    /// <summary>Save the in-memory model back to its file. Word and Excel are editable; PowerPoint is read-only.</summary>
    public static void Save(OfficeDocument doc)
    {
        switch (doc.Kind)
        {
            case OfficeDocumentKind.Word: SaveWord(doc); break;
            case OfficeDocumentKind.Excel: SaveExcel(doc); break;
            case OfficeDocumentKind.PowerPoint:
                throw new NotSupportedException("PowerPoint outline editing is not supported; open the file to view it.");
            default: throw new NotSupportedException("Unknown document type.");
        }
    }

    // ---------------------------------------------------------------- Word

    private static void LoadWord(OfficeDocument doc)
    {
        using var package = WordprocessingDocument.Open(doc.Path, false);
        var body = package.MainDocumentPart?.Document?.Body;
        if (body is null) return;

        foreach (var paragraph in body.Elements<W.Paragraph>())
        {
            var text = string.Concat(paragraph.Descendants<W.Text>().Select(t => t.Text));
            var style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "Normal";
            var runProps = paragraph.Descendants<W.RunProperties>().FirstOrDefault();
            doc.Paragraphs.Add(new WordParagraph
            {
                Text = text,
                Style = NormaliseWordStyle(style),
                IsBold = runProps?.GetFirstChild<W.Bold>() is not null,
                IsItalic = runProps?.GetFirstChild<W.Italic>() is not null,
                IsUnderline = runProps?.GetFirstChild<W.Underline>() is not null
            });
        }

        if (doc.Paragraphs.Count == 0)
            doc.Paragraphs.Add(new WordParagraph());
    }

    private static string NormaliseWordStyle(string? styleId) => styleId switch
    {
        null or "" or "Normal" => "Normal",
        "Title" => "Title",
        var s when s.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) => s,
        _ => "Normal"
    };

    private static void SaveWord(OfficeDocument doc)
    {
        using var package = WordprocessingDocument.Open(doc.Path, true);
        var body = package.MainDocumentPart?.Document?.Body
            ?? throw new InvalidOperationException("Document body is missing.");

        var sectPr = body.GetFirstChild<W.SectionProperties>();
        foreach (var paragraph in body.Elements<W.Paragraph>().ToList())
            paragraph.Remove();

        foreach (var model in doc.Paragraphs)
        {
            var paragraph = new W.Paragraph();
            if (model.Style != "Normal")
                paragraph.AppendChild(new W.ParagraphProperties(
                    new W.ParagraphStyleId { Val = model.Style }));

            var run = new W.Run();
            if (model.IsBold || model.IsItalic || model.IsUnderline)
            {
                var props = new W.RunProperties();
                if (model.IsBold) props.AppendChild(new W.Bold());
                if (model.IsItalic) props.AppendChild(new W.Italic());
                if (model.IsUnderline) props.AppendChild(new W.Underline { Val = W.UnderlineValues.Single });
                run.AppendChild(props);
            }
            run.AppendChild(new W.Text { Text = model.Text, Space = SpaceProcessingModeValues.Preserve });
            paragraph.AppendChild(run);

            if (sectPr is not null) body.InsertBefore(paragraph, sectPr);
            else body.AppendChild(paragraph);
        }
        package.MainDocumentPart!.Document.Save();
    }

    // ---------------------------------------------------------------- Excel

    private static void LoadExcel(OfficeDocument doc)
    {
        using var package = SpreadsheetDocument.Open(doc.Path, false);
        var workbookPart = package.WorkbookPart
            ?? throw new InvalidOperationException("Workbook part is missing.");
        var shared = workbookPart.SharedStringTablePart?.SharedStringTable;

        var sheets = workbookPart.Workbook.Sheets?.Elements<S.Sheet>().ToList() ?? new List<S.Sheet>();
        foreach (var sheet in sheets)
        {
            var model = new ExcelSheet { Name = sheet.Name?.Value ?? "Sheet" };
            if (sheet.Id?.Value is not null &&
                workbookPart.GetPartById(sheet.Id.Value) is WorksheetPart wsPart)
            {
                var data = wsPart.Worksheet.GetFirstChild<S.SheetData>();
                if (data is not null)
                {
                    foreach (var cell in data.Elements<S.Row>().SelectMany(r => r.Elements<S.Cell>()))
                    {
                        var (row, col) = ParseReference(cell.CellReference?.Value);
                        if (row < 1 || col < 1) continue;
                        model.Cells.Add(new ExcelCell
                        {
                            Reference = cell.CellReference?.Value ?? CellReference(row, col),
                            Row = row,
                            Column = col,
                            Text = CellText(cell, shared)
                        });
                    }
                }
            }

            // Pad the grid so empty cells are visible and editable.
            var maxRow = Math.Max(MinGridRows, model.Cells.Count == 0 ? 0 : model.Cells.Max(c => c.Row));
            var maxCol = Math.Max(MinGridColumns, model.Cells.Count == 0 ? 0 : model.Cells.Max(c => c.Column));
            var existing = model.Cells.ToDictionary(c => (c.Row, c.Column));
            for (var r = 1; r <= maxRow; r++)
            for (var c = 1; c <= maxCol; c++)
            {
                if (existing.ContainsKey((r, c))) continue;
                model.Cells.Add(new ExcelCell { Row = r, Column = c, Reference = CellReference(r, c) });
            }
            doc.Sheets.Add(model);
        }

        if (doc.Sheets.Count == 0)
            doc.Sheets.Add(new ExcelSheet { Name = "Sheet1" });
    }

    private static string CellText(S.Cell cell, S.SharedStringTable? shared)
    {
        var raw = cell.CellValue?.InnerText ?? string.Empty;
        if (cell.DataType?.Value == S.CellValues.SharedString &&
            int.TryParse(raw, out var index) &&
            shared is not null)
        {
            var item = shared.Elements<S.SharedStringItem>().ElementAtOrDefault(index);
            return item?.InnerText ?? string.Empty;
        }
        if (cell.DataType?.Value == S.CellValues.InlineString)
            return cell.Elements<S.InlineString>().FirstOrDefault()?.InnerText ?? string.Empty;
        return raw;
    }

    private static (int Row, int Column) ParseReference(string? reference)
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

    private static string CellReference(int row, int column)
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

    private static void SaveExcel(OfficeDocument doc)
    {
        using var package = SpreadsheetDocument.Open(doc.Path, true);
        var workbookPart = package.WorkbookPart
            ?? throw new InvalidOperationException("Workbook part is missing.");
        var sheets = workbookPart.Workbook.Sheets?.Elements<S.Sheet>().ToList() ?? new List<S.Sheet>();

        foreach (var sheetModel in doc.Sheets)
        {
            var sheet = sheets.FirstOrDefault(s =>
                string.Equals(s.Name?.Value, sheetModel.Name, StringComparison.OrdinalIgnoreCase));
            if (sheet?.Id?.Value is null ||
                workbookPart.GetPartById(sheet.Id.Value) is not WorksheetPart wsPart) continue;

            var data = wsPart.Worksheet.GetFirstChild<S.SheetData>()
                ?? wsPart.Worksheet.AppendChild(new S.SheetData());

            foreach (var cellModel in sheetModel.Cells.Where(c => !string.IsNullOrEmpty(c.Text)))
            {
                var cell = data.Elements<S.Row>()
                    .SelectMany(r => r.Elements<S.Cell>())
                    .FirstOrDefault(c => string.Equals(c.CellReference?.Value, cellModel.Reference, StringComparison.OrdinalIgnoreCase));
                if (cell is null)
                {
                    var row = EnsureRow(data, (uint)cellModel.Row);
                    cell = new S.Cell { CellReference = cellModel.Reference };
                    InsertCellInOrder(row, cell);
                }
                cell.RemoveAllChildren();
                cell.DataType = S.CellValues.InlineString;
                cell.AppendChild(new S.InlineString(new S.Text { Text = cellModel.Text, Space = SpaceProcessingModeValues.Preserve }));
            }
            wsPart.Worksheet.Save();
        }
    }

    private static S.Row EnsureRow(S.SheetData data, uint rowIndex)
    {
        var row = data.Elements<S.Row>().FirstOrDefault(r => r.RowIndex?.Value == rowIndex);
        if (row is not null) return row;
        row = new S.Row { RowIndex = rowIndex };
        var after = data.Elements<S.Row>().FirstOrDefault(r => (r.RowIndex?.Value ?? 0) > rowIndex);
        if (after is not null) data.InsertBefore(row, after);
        else data.AppendChild(row);
        return row;
    }

    private static void InsertCellInOrder(S.Row row, S.Cell cell)
    {
        var col = ParseReference(cell.CellReference?.Value).Column;
        var after = row.Elements<S.Cell>().FirstOrDefault(c => ParseReference(c.CellReference?.Value).Column > col);
        if (after is not null) row.InsertBefore(cell, after);
        else row.AppendChild(cell);
    }

    // ---------------------------------------------------------------- PowerPoint

    private static void LoadPowerPoint(OfficeDocument doc)
    {
        using var package = PresentationDocument.Open(doc.Path, false);
        var presentationPart = package.PresentationPart
            ?? throw new InvalidOperationException("Presentation part is missing.");

        var number = 1;
        foreach (var slidePart in presentationPart.SlideParts)
        {
            var slide = new PowerPointSlide { Number = number++ };
            var shapeTree = slidePart.Slide.CommonSlideData?.ShapeTree;
            if (shapeTree is not null)
            {
                foreach (var shape in shapeTree.Elements<P.Shape>())
                {
                    // OpenXML 3.x may parse a:txBody inside p:sp as an unknown element; walking by
                    // local name keeps this working across SDK versions.
                    var textBody = shape.ChildElements.FirstOrDefault(e => e.LocalName == "txBody");
                    if (textBody is null) continue;
                    var lines = textBody.ChildElements
                        .Where(e => e.LocalName == "p")
                        .Select(e => e.InnerText.Trim())
                        .Where(t => !string.IsNullOrWhiteSpace(t))
                        .ToList();
                    if (lines.Count == 0) continue;

                    var ph = shape.ChildElements.FirstOrDefault(e => e.LocalName == "nvSpPr")
                        ?.ChildElements.FirstOrDefault(e => e.LocalName == "nvPr")
                        ?.ChildElements.FirstOrDefault(e => e.LocalName == "ph");
                    var phType = ph?.OuterXml.Contains("type=\"title\"") == true ||
                                 ph?.OuterXml.Contains("type='title'") == true
                        ? "title"
                        : ph?.OuterXml.Contains("type=\"ctrTitle\"") == true ||
                          ph?.OuterXml.Contains("type='ctrTitle'") == true
                            ? "ctrTitle"
                            : string.Empty;
                    var isTitle = phType is "title" or "ctrTitle";

                    if (isTitle && string.IsNullOrEmpty(slide.Title))
                        slide.Title = lines[0];
                    else
                    {
                        if (!string.IsNullOrEmpty(slide.Title) || slide.Lines.Count > 0)
                            slide.Lines.AddRange(lines);
                        else
                            slide.Title = lines[0];
                    }
                }
            }
            doc.Slides.Add(slide);
        }
    }

    // ---------------------------------------------------------------- New documents

    /// <summary>Create a blank document. PowerPoint creation is not supported (view-only).</summary>
    public static void CreateNew(string path, OfficeDocumentKind kind)
    {
        switch (kind)
        {
            case OfficeDocumentKind.Word:
                using (var word = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
                {
                    var main = word.AddMainDocumentPart();
                    main.Document = new W.Document(new W.Body(
                        new W.Paragraph(new W.Run(new W.Text(string.Empty))),
                        new W.SectionProperties(
                            new W.PageSize { Width = 12240U, Height = 15840U },
                            new W.PageMargin { Top = 1440, Right = 1440, Bottom = 1440, Left = 1440 })));
                    main.Document.Save();
                }
                break;
            case OfficeDocumentKind.Excel:
                using (var excel = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
                {
                    var wbPart = excel.AddWorkbookPart();
                    wbPart.Workbook = new S.Workbook(new S.Sheets(
                        new S.Sheet { Name = "Sheet1", SheetId = 1U, Id = "rId1" }));
                    var wsPart = wbPart.AddNewPart<WorksheetPart>("rId1");
                    wsPart.Worksheet = new S.Worksheet(new S.SheetData());
                    wbPart.Workbook.Save();
                    wsPart.Worksheet.Save();
                }
                break;
            default:
                throw new NotSupportedException("Creating PowerPoint documents is not supported.");
        }
    }
}
