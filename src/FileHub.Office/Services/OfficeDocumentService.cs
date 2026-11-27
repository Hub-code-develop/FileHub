using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using FileHub.Office.Models;
using A = DocumentFormat.OpenXml.Drawing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;
using WP = DocumentFormat.OpenXml.Drawing.Wordprocessing;

namespace FileHub.Office.Services;

/// <summary>
/// Reads and writes docx / xlsx / pptx through the Open XML SDK. No COM and no Office install:
/// the same files open in Word, Excel and PowerPoint on Windows, macOS and Linux.
/// </summary>
public static partial class OfficeDocumentService
{
    internal const int DefaultRows = 60;
    internal const int DefaultColumns = 20;

    public static bool IsOfficeFile(string path) => Detect(path) != OfficeDocumentKind.Unknown;

    public static OfficeDocumentKind Detect(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return OfficeDocumentKind.Unknown;
        return ext.ToLowerInvariant() switch
        {
            ".docx" or ".docm" => OfficeDocumentKind.Word,
            ".xlsx" or ".xlsm" => OfficeDocumentKind.Excel,
            ".pptx" or ".pptm" => OfficeDocumentKind.PowerPoint,
            ".wps" or ".wpt" => OfficeDocumentKind.Word,
            ".et" or ".ett" => OfficeDocumentKind.Excel,
            ".dps" or ".dpt" => OfficeDocumentKind.PowerPoint,
            _ => OfficeDocumentKind.Unknown
        };
    }

    public static OfficeDocument Load(string path)
    {
        if (WpsFormatService.IsWpsFile(path)) return WpsFormatService.Load(path);
        var kind = Detect(path);
        var doc = new OfficeDocument { Path = path, Kind = kind };
        switch (kind)
        {
            case OfficeDocumentKind.Word: LoadWord(doc); break;
            case OfficeDocumentKind.Excel: LoadExcel(doc); break;
            case OfficeDocumentKind.PowerPoint: LoadPowerPoint(doc); break;
            default: throw new NotSupportedException($"Unsupported Office file: {path}");
        }
        if (kind == OfficeDocumentKind.Excel)
        {
            ChartService.Read(doc);
            PivotService.Read(doc);
        }
        OleObjectService.Read(doc);
        MacroService.Read(doc);
        return doc;
    }

    public static void Save(OfficeDocument doc)
    {
        switch (doc.Kind)
        {
            case OfficeDocumentKind.Word: SaveWord(doc); break;
            case OfficeDocumentKind.Excel: SaveExcel(doc); break;
            case OfficeDocumentKind.PowerPoint: SavePowerPoint(doc); break;
            default: throw new NotSupportedException("Unknown document type.");
        }
        if (doc.Kind == OfficeDocumentKind.Excel)
        {
            ChartService.Write(doc);
            PivotService.Write(doc);
        }
        OleObjectService.Sync(doc);
    }

    public static void CreateNew(string path, OfficeDocumentKind kind)
    {
        switch (kind)
        {
            case OfficeDocumentKind.Word: CreateWord(path); break;
            case OfficeDocumentKind.Excel: CreateExcel(path); break;
            case OfficeDocumentKind.PowerPoint: CreatePowerPoint(path); break;
            default: throw new NotSupportedException("Unknown document type.");
        }
    }

    // ---------------------------------------------------------------- Word: load

    private static void LoadWord(OfficeDocument doc)
    {
        using var package = WordprocessingDocument.Open(doc.Path, false);
        var main = package.MainDocumentPart;
        var body = main?.Document?.Body;
        if (body is null) return;

        var numbering = main!.NumberingDefinitionsPart?.Numbering;
        var hyperlinks = main.HyperlinkRelationships.ToDictionary(r => r.Id, r => r.Uri?.OriginalString ?? string.Empty);

        foreach (var element in body.ChildElements)
        {
            switch (element)
            {
                case W.Paragraph paragraph:
                    var images = new List<WordImage>();
                    var block = ReadWordParagraph(paragraph, numbering, hyperlinks, main!, images);
                    if (block is WordParagraph textBlock && textBlock.PlainText.Length == 0 && images.Count > 0)
                    {
                        // The paragraph only carried the inline picture.
                    }
                    else doc.Blocks.Add(block);
                    foreach (var image in images) doc.Blocks.Add(image);
                    break;
                case W.Table table:
                    doc.Blocks.Add(ReadWordTable(table));
                    break;
            }
        }

        if (doc.Blocks.Count == 0)
            doc.Blocks.Add(new WordParagraph());

        WordNumberingService.Read(doc, main);
        WordNumberingService.EnsureDefaults(doc);
        WordNumberingService.RefreshLabels(doc);
        RevisionService.Read(doc, main);
    }

    private static WordBlock ReadWordParagraph(W.Paragraph paragraph, W.Numbering? numbering,
        IReadOnlyDictionary<string, string> hyperlinks, MainDocumentPart main, List<WordImage> images)
    {
        var result = new WordParagraph();
        var props = paragraph.ParagraphProperties;
        if (props is not null)
        {
            result.Style = NormaliseWordStyle(props.ParagraphStyleId?.Val?.Value);
            var justification = props.Justification?.Val?.Value;
            result.Alignment = justification == W.JustificationValues.Center ? WordAlignment.Center
                : justification == W.JustificationValues.Right ? WordAlignment.Right
                : justification == W.JustificationValues.Both ? WordAlignment.Justify
                : WordAlignment.Left;
            result.LeftIndent = TwipsToPoints(props.Indentation?.Left?.Value);
            var spacing = props.SpacingBetweenLines;
            result.SpaceBefore = TwipsToPoints(spacing?.Before?.Value);
            result.SpaceAfter = TwipsToPoints(spacing?.After?.Value);

            var numberingProps = props.NumberingProperties;
            if (numberingProps is not null)
            {
                var level = (int)(numberingProps.NumberingLevelReference?.Val?.Value ?? 0);
                result.ListLevel = level;
                var numId = numberingProps.NumberingId?.Val?.Value;
                result.NumberId = numId ?? 0;
                result.ListKind = IsBullet(numbering, numId, level) ? WordListKind.Bullet : WordListKind.Number;
            }
        }

        foreach (var run in paragraph.Elements<W.Run>())
        {
            var model = ReadRun(run);
            if (!string.IsNullOrEmpty(model.Text)) result.Runs.Add(model);
        }

        foreach (var link in paragraph.Elements<W.Hyperlink>())
        {
            var url = link.Id?.Value is { } id && hyperlinks.TryGetValue(id, out var target) ? target : string.Empty;
            foreach (var run in link.Elements<W.Run>())
            {
                var model = ReadRun(run);
                model.Hyperlink = url;
                if (!string.IsNullOrEmpty(model.Text)) result.Runs.Add(model);
            }
        }

        // A picture lives inside a run's drawing. Promote it to a block so it can be displayed.
        var blip = paragraph.Descendants<A.Blip>().FirstOrDefault(b => !string.IsNullOrEmpty(b.Embed?.Value));
        if (blip?.Embed?.Value is { } embedId && main.GetPartById(embedId) is ImagePart imagePart)
        {
            using var stream = imagePart.GetStream(FileMode.Open, FileAccess.Read);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var extent = paragraph.Descendants<WP.Extent>().FirstOrDefault();
            images.Add(new WordImage
            {
                Path = string.Empty,
                Data = memory.ToArray(),
                ContentType = imagePart.ContentType,
                WidthPx = extent?.Cx is { } cx ? (int)(cx / 9525) : 480,
                HeightPx = extent?.Cy is { } cy ? (int)(cy / 9525) : 270
            });
        }

        return result;
    }

    private static WordRun ReadRun(W.Run run)
    {
        var model = new WordRun();
        var props = run.RunProperties;
        if (props is not null)
        {
            model.IsBold = props.GetFirstChild<W.Bold>() is not null;
            model.IsItalic = props.GetFirstChild<W.Italic>() is not null;
            model.IsUnderline = props.GetFirstChild<W.Underline>() is not null;
            model.FontName = props.RunFonts?.Ascii?.Value ?? string.Empty;
            if (double.TryParse(props.FontSize?.Val?.Value, out var size)) model.FontSize = size / 2;
            model.ColorHex = props.Color?.Val?.Value ?? string.Empty;
        }
        model.Text = string.Concat(run.Descendants<W.Text>().Select(t => t.Text ?? string.Empty));
        if (run.Descendants<W.Break>().Any()) model.Text += "\n";
        return model;
    }

    private static WordTable ReadWordTable(W.Table table)
    {
        var result = WordTable.Create(0, Math.Max(1, table.GetFirstChild<W.TableGrid>()?.Elements<W.GridColumn>().Count() ?? 1));
        result.Rows.Clear();
        foreach (var row in table.Elements<W.TableRow>())
        {
            var cells = new List<WordTableCell>();
            foreach (var cell in row.Elements<W.TableCell>())
            {
                cells.Add(new WordTableCell { Text = string.Concat(cell.Descendants<W.Text>().Select(t => t.Text ?? string.Empty)) });
            }
            result.Rows.Add(cells);
        }
        return result;
    }

    private static bool IsBullet(W.Numbering? numbering, int? numId, int level)
    {
        if (numbering is null || numId is null) return false;
        var num = numbering.Elements<W.NumberingInstance>().FirstOrDefault(n => n.NumberID?.Value == numId);
        var abstractId = num?.AbstractNumId?.Val?.Value;
        var abs = numbering.Elements<W.AbstractNum>().FirstOrDefault(a => a.AbstractNumberId?.Value == abstractId);
        var lvl = abs?.Elements<W.Level>().FirstOrDefault(l => l.LevelIndex?.Value == level);
        return lvl?.OuterXml.Contains("bullet", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string NormaliseWordStyle(string? styleId) => styleId switch
    {
        null or "" or "Normal" => "Normal",
        "Title" => "Title",
        var s when s.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) => s,
        _ => "Normal"
    };

    private static double TwipsToPoints(string? value) =>
        int.TryParse(value, out var twips) ? twips / 20.0 : 0;

    private static int PointsToTwips(double points) => (int)Math.Round(points * 20);

    // ---------------------------------------------------------------- Word: save

    private static void SaveWord(OfficeDocument doc)
    {
        using var package = WordprocessingDocument.Open(doc.Path, true);
        var main = package.MainDocumentPart ?? throw new InvalidOperationException("Document body is missing.");
        var body = main.Document?.Body ?? throw new InvalidOperationException("Document body is missing.");
        var section = body.GetFirstChild<W.SectionProperties>();
        foreach (var child in body.ChildElements.ToList())
            if (child is not W.SectionProperties) child.Remove();

        foreach (var block in doc.Blocks)
        {
            OpenXmlElement? element = block switch
            {
                WordParagraph paragraph => CreateWordParagraph(main, paragraph),
                WordTable table => CreateWordTable(table),
                WordImage image => CreateWordImage(main, image),
                _ => null
            };
            if (element is null) continue;
            if (section is not null) body.InsertBefore(element, section);
            else body.AppendChild(element);
        }
        WordNumberingService.Write(doc, main);
        RevisionService.Write(doc, main);
        main.Document.Save();
    }

    private static W.Paragraph CreateWordParagraph(MainDocumentPart main, WordParagraph model)
    {
        var paragraph = new W.Paragraph();
        var props = new W.ParagraphProperties();
        if (model.Style != "Normal")
            props.AppendChild(new W.ParagraphStyleId { Val = model.Style });

        var justification = model.Alignment == WordAlignment.Center ? W.JustificationValues.Center
            : model.Alignment == WordAlignment.Right ? W.JustificationValues.Right
            : model.Alignment == WordAlignment.Justify ? W.JustificationValues.Both
            : W.JustificationValues.Left;
        props.AppendChild(new W.Justification { Val = justification });

        if (model.ListKind != WordListKind.None)
        {
            props.AppendChild(new W.Indentation
            {
                Left = PointsToTwips(36 * (model.ListLevel + 1)).ToString(),
                Hanging = "360"
            });
            props.AppendChild(new W.NumberingProperties(
                new W.NumberingLevelReference { Val = model.ListLevel },
                new W.NumberingId
                {
                    Val = model.NumberId > 0
                        ? model.NumberId
                        : model.ListKind == WordListKind.Bullet ? 1 : 2
                }));
        }
        else if (model.LeftIndent != 0)
        {
            props.AppendChild(new W.Indentation { Left = PointsToTwips(model.LeftIndent).ToString() });
        }

        if (model.SpaceBefore != 0 || model.SpaceAfter != 0)
        {
            var spacing = new W.SpacingBetweenLines();
            if (model.SpaceBefore != 0) spacing.Before = PointsToTwips(model.SpaceBefore).ToString();
            if (model.SpaceAfter != 0) spacing.After = PointsToTwips(model.SpaceAfter).ToString();
            props.AppendChild(spacing);
        }

        if (props.ChildElements.Count > 0) paragraph.AppendChild(props);

        foreach (var runModel in model.Runs)
        {
            var run = CreateRun(runModel);
            OpenXmlElement container = paragraph;
            if (runModel.RevisionId > 0)
            {
                container = new W.InsertedRun
                {
                    Author = RevisionService.Author,
                    Date = new DocumentFormat.OpenXml.DateTimeValue(DateTime.UtcNow),
                    Id = runModel.RevisionId.ToString()
                };
            }
            if (!string.IsNullOrWhiteSpace(runModel.Hyperlink) &&
                Uri.TryCreate(runModel.Hyperlink, UriKind.Absolute, out var uri))
            {
                var relationship = main.AddHyperlinkRelationship(uri, true);
                var linkRun = new W.Run();
                if (run.RunProperties is not null) linkRun.AppendChild(run.RunProperties.CloneNode(true));
                linkRun.AppendChild(new W.Text
                {
                    Text = runModel.Text,
                    Space = SpaceProcessingModeValues.Preserve
                });
                container.AppendChild(new W.Hyperlink(linkRun) { Id = relationship.Id });
            }
            else
            {
                container.AppendChild(run);
            }
            if (container != paragraph) paragraph.AppendChild(container);
        }
        if (model.Runs.Count == 0) paragraph.AppendChild(new W.Run());
        return paragraph;
    }

    private static W.Run CreateRun(WordRun model)
    {
        var run = new W.Run();
        if (model.IsBold || model.IsItalic || model.IsUnderline || model.FontSize > 0 ||
            !string.IsNullOrEmpty(model.FontName) || !string.IsNullOrEmpty(model.ColorHex))
        {
            var props = new W.RunProperties();
            if (model.IsBold) props.AppendChild(new W.Bold());
            if (model.IsItalic) props.AppendChild(new W.Italic());
            if (model.IsUnderline) props.AppendChild(new W.Underline { Val = W.UnderlineValues.Single });
            if (!string.IsNullOrEmpty(model.FontName))
                props.AppendChild(new W.RunFonts { Ascii = model.FontName, HighAnsi = model.FontName });
            if (model.FontSize > 0)
                props.AppendChild(new W.FontSize { Val = ((int)Math.Round(model.FontSize * 2)).ToString() });
            if (!string.IsNullOrEmpty(model.ColorHex))
                props.AppendChild(new W.Color { Val = model.ColorHex });
            run.AppendChild(props);
        }
        run.AppendChild(new W.Text { Text = model.Text, Space = SpaceProcessingModeValues.Preserve });
        return run;
    }

    private static W.Table CreateWordTable(WordTable model)
    {
        var table = new W.Table(
            new W.TableProperties(
                new W.TableStyle { Val = "TableGrid" },
                new W.TableBorders(
                    new W.TopBorder { Val = W.BorderValues.Single, Size = 4U },
                    new W.LeftBorder { Val = W.BorderValues.Single, Size = 4U },
                    new W.BottomBorder { Val = W.BorderValues.Single, Size = 4U },
                    new W.RightBorder { Val = W.BorderValues.Single, Size = 4U },
                    new W.InsideHorizontalBorder { Val = W.BorderValues.Single, Size = 4U },
                    new W.InsideVerticalBorder { Val = W.BorderValues.Single, Size = 4U })),
            new W.TableGrid(Enumerable.Range(0, model.ColumnCount)
                .Select(_ => new W.GridColumn { Width = "2400" })));

        foreach (var row in model.Rows)
        {
            var tableRow = new W.TableRow();
            for (var c = 0; c < model.ColumnCount; c++)
            {
                var text = row.ElementAtOrDefault(c)?.Text ?? string.Empty;
                tableRow.AppendChild(new W.TableCell(new W.Paragraph(
                    new W.Run(new W.Text { Text = text, Space = SpaceProcessingModeValues.Preserve }))));
            }
            table.AppendChild(tableRow);
        }
        return table;
    }

    private static W.Paragraph CreateWordImage(MainDocumentPart main, WordImage model)
    {
        var contentType = string.IsNullOrWhiteSpace(model.ContentType) ? "image/png" : model.ContentType;
        var part = main.AddNewPart<ImagePart>(contentType);
        using (var stream = part.GetStream(FileMode.Create, FileAccess.Write))
            stream.Write(model.Data);
        var relId = main.GetIdOfPart(part);

        var cx = (long)Math.Max(1, model.WidthPx) * 9525;
        var cy = (long)Math.Max(1, model.HeightPx) * 9525;
        var docPrId = (uint)(main.RootElement?.Descendants<WP.DocProperties>().Count() + 1 ?? 1);

        var drawing = new W.Drawing(
            new WP.Inline(
                new WP.Extent { Cx = cx, Cy = cy },
                new WP.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
                new WP.DocProperties { Id = docPrId, Name = "Picture " + docPrId, Description = "FileHub image" },
                new WP.NonVisualGraphicFrameDrawingProperties(),
                new A.Graphic(
                    new A.GraphicData(
                        new PIC.Picture(
                            new PIC.NonVisualPictureProperties(
                                new PIC.NonVisualDrawingProperties { Id = 0U, Name = "Picture " + docPrId },
                                new PIC.NonVisualPictureDrawingProperties()),
                            new PIC.BlipFill(
                                new A.Blip { Embed = relId },
                                new A.Stretch(new A.FillRectangle())),
                            new PIC.ShapeProperties(
                                new A.Transform2D(
                                    new A.Offset { X = 0L, Y = 0L },
                                    new A.Extents { Cx = cx, Cy = cy }),
                                new A.PresetGeometry(new A.AdjustValueList())
                                { Preset = A.ShapeTypeValues.Rectangle }))
                    ) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" })
            ) { DistanceFromTop = 0U, DistanceFromBottom = 0U, DistanceFromLeft = 0U, DistanceFromRight = 0U });
        return new W.Paragraph(new W.Run(drawing));
    }

    // ---------------------------------------------------------------- Excel: load

    private static void LoadExcel(OfficeDocument doc)
    {
        using var package = SpreadsheetDocument.Open(doc.Path, false);
        var wbPart = package.WorkbookPart ?? throw new InvalidOperationException("Workbook part is missing.");
        var shared = wbPart.SharedStringTablePart?.SharedStringTable;
        var styles = wbPart.WorkbookStylesPart?.Stylesheet;

        foreach (var sheet in wbPart.Workbook.Sheets?.Elements<S.Sheet>() ?? Enumerable.Empty<S.Sheet>())
        {
            var model = new ExcelSheet
            {
                Name = sheet.Name?.Value ?? "Sheet",
                RowCount = DefaultRows,
                ColumnCount = DefaultColumns
            };
            if (sheet.Id?.Value is { } id && wbPart.GetPartById(id) is WorksheetPart wsPart)
            {
                var columns = wsPart.Worksheet.GetFirstChild<S.Columns>();
                foreach (var col in columns?.Elements<S.Column>() ?? Enumerable.Empty<S.Column>())
                {
                    if (col.Min is null || col.Max is null) continue;
                    var width = col.Width?.Value ?? 8.43;
                    for (var c = (int)col.Min.Value; c <= (int)col.Max.Value && c <= 60; c++)
                        while (model.ColumnWidths.Count < c) model.ColumnWidths.Add(width);
                }

                foreach (var cell in wsPart.Worksheet.GetFirstChild<S.SheetData>()?.Elements<S.Row>()
                             .SelectMany(r => r.Elements<S.Cell>()) ?? Enumerable.Empty<S.Cell>())
                {
                    var (row, column) = ExcelSheet.ParseReference(cell.CellReference?.Value);
                    if (row < 1 || column < 1) continue;
                    var cellModel = model.Ensure(row, column);
                    cellModel.Display = CellText(cell, shared);
                    cellModel.Formula = cell.CellFormula?.Text ?? string.Empty;
                    cellModel.NumberFormat = NumberFormatFor(styles, cell.StyleIndex?.Value);
                    ApplyCellStyle(cellModel, styles, cell.StyleIndex?.Value);
                }
            }
            while (model.ColumnWidths.Count < DefaultColumns) model.ColumnWidths.Add(110);
            doc.Sheets.Add(model);
        }

        if (doc.Sheets.Count == 0)
            doc.Sheets.Add(new ExcelSheet { Name = "Sheet1" });
    }

    private static string CellText(S.Cell cell, S.SharedStringTable? shared)
    {
        var raw = cell.CellValue?.InnerText ?? string.Empty;
        if (cell.DataType?.Value == S.CellValues.SharedString &&
            int.TryParse(raw, out var index) && shared is not null)
            return shared.Elements<S.SharedStringItem>().ElementAtOrDefault(index)?.InnerText ?? string.Empty;
        if (cell.DataType?.Value == S.CellValues.InlineString)
            return cell.Elements<S.InlineString>().FirstOrDefault()?.InnerText ?? string.Empty;
        return raw;
    }

    private static string NumberFormatFor(S.Stylesheet? styles, uint? styleIndex)
    {
        if (styles is null || styleIndex is null) return string.Empty;
        var xfs = styles.GetFirstChild<S.CellFormats>();
        var xf = xfs?.Elements<S.CellFormat>().ElementAtOrDefault((int)styleIndex.Value);
        if (xf?.NumberFormatId?.Value is not { } id) return string.Empty;
        if (id < 164) return string.Empty;
        return styles.GetFirstChild<S.NumberingFormats>()?.Elements<S.NumberingFormat>()
            .FirstOrDefault(n => n.NumberFormatId?.Value == id)?.FormatCode?.Value ?? string.Empty;
    }

    private static void ApplyCellStyle(ExcelCell cell, S.Stylesheet? styles, uint? styleIndex)
    {
        if (styles is null || styleIndex is null) return;
        var xf = styles.GetFirstChild<S.CellFormats>()?.Elements<S.CellFormat>().ElementAtOrDefault((int)styleIndex.Value);
        if (xf?.FontId?.Value is not { } fontId) return;
        var font = styles.GetFirstChild<S.Fonts>()?.Elements<S.Font>().ElementAtOrDefault((int)fontId);
        if (font is null) return;
        cell.IsBold = font.GetFirstChild<S.Bold>() is not null;
        cell.IsItalic = font.GetFirstChild<S.Italic>() is not null;
        cell.IsUnderline = font.GetFirstChild<S.Underline>() is not null;
        cell.FontColor = StripAlpha(font.GetFirstChild<S.Color>()?.Rgb?.Value);
        if (xf.FillId?.Value is { } fillId)
        {
            var fill = styles.GetFirstChild<S.Fills>()?.Elements<S.Fill>().ElementAtOrDefault((int)fillId);
            cell.FillColor = StripAlpha(fill?.PatternFill?.ForegroundColor?.Rgb?.Value);
        }
        var horizontal = xf.Alignment?.Horizontal?.Value;
        cell.Alignment = horizontal == S.HorizontalAlignmentValues.Center ? WordAlignment.Center
            : horizontal == S.HorizontalAlignmentValues.Right ? WordAlignment.Right
            : horizontal == S.HorizontalAlignmentValues.Justify ? WordAlignment.Justify
            : WordAlignment.Left;
    }

    // ---------------------------------------------------------------- Excel: save

    private static void SaveExcel(OfficeDocument doc)
    {
        using var package = SpreadsheetDocument.Open(doc.Path, true);
        var wbPart = package.WorkbookPart ?? throw new InvalidOperationException("Workbook part is missing.");
        var sheetsElement = wbPart.Workbook.Sheets ?? wbPart.Workbook.AppendChild(new S.Sheets());
        var existing = sheetsElement.Elements<S.Sheet>().ToList();

        // Remove worksheets that were deleted in the editor.
        foreach (var sheet in existing)
        {
            if (doc.Sheets.Any(s => string.Equals(s.Name, sheet.Name?.Value, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (sheet.Id?.Value is { } id && wbPart.GetPartById(id) is WorksheetPart oldPart)
                wbPart.DeletePart(oldPart);
            sheet.Remove();
        }

        foreach (var model in doc.Sheets)
        {
            var sheet = sheetsElement.Elements<S.Sheet>().FirstOrDefault(s =>
                string.Equals(s.Name?.Value, model.Name, StringComparison.OrdinalIgnoreCase));
            WorksheetPart wsPart;
            if (sheet is null)
            {
                wsPart = wbPart.AddNewPart<WorksheetPart>();
                var maxSheetId = 0U;
                foreach (var candidate in sheetsElement.Elements<S.Sheet>())
                    maxSheetId = Math.Max(maxSheetId, candidate.SheetId?.Value ?? 0U);
                var newId = maxSheetId + 1;
                sheet = new S.Sheet { Name = model.Name, SheetId = newId, Id = wbPart.GetIdOfPart(wsPart) };
                sheetsElement.AppendChild(sheet);
                wsPart.Worksheet = new S.Worksheet(new S.SheetData());
            }
            else
            {
                if (sheet.Id?.Value is not { } id || wbPart.GetPartById(id) is not WorksheetPart found) continue;
                wsPart = found;
                wsPart.Worksheet ??= new S.Worksheet();
            }

            sheet.Name = model.Name;
            WriteWorksheet(wbPart, wsPart, model);
        }
        wbPart.Workbook.Save();
    }

    private static void WriteWorksheet(WorkbookPart wbPart, WorksheetPart wsPart, ExcelSheet model)
    {
        var worksheet = wsPart.Worksheet;
        var data = worksheet.GetFirstChild<S.SheetData>() ?? worksheet.AppendChild(new S.SheetData());

        // A clean SheetData keeps row/column insertion and deletion consistent with the model.
        data.RemoveAllChildren();
        foreach (var group in model.Cells.Where(c => c.HasFormula || !string.IsNullOrEmpty(c.Display))
                     .OrderBy(c => c.Row).ThenBy(c => c.Column).GroupBy(c => c.Row))
        {
            var row = new S.Row { RowIndex = (uint)group.Key };
            foreach (var cellModel in group.OrderBy(c => c.Column))
            {
                var cell = new S.Cell { CellReference = cellModel.Reference };
                var styleIndex = EnsureCellStyle(wbPart, cellModel);
                if (styleIndex > 0) cell.StyleIndex = styleIndex;

                if (cellModel.HasFormula)
                {
                    cell.AppendChild(new S.CellFormula { Text = cellModel.Formula.TrimStart('=') });
                    cell.AppendChild(new S.CellValue { Text = cellModel.Display });
                }
                else if (double.TryParse(cellModel.Display, out var number))
                {
                    cell.AppendChild(new S.CellValue { Text = number.ToString(System.Globalization.CultureInfo.InvariantCulture) });
                }
                else
                {
                    cell.DataType = S.CellValues.InlineString;
                    cell.AppendChild(new S.InlineString(new S.Text
                    {
                        Text = cellModel.Display,
                        Space = SpaceProcessingModeValues.Preserve
                    }));
                }
                row.AppendChild(cell);
            }
            data.AppendChild(row);
        }

        var columns = worksheet.GetFirstChild<S.Columns>();
        if (columns is not null) columns.Remove();
        if (model.ColumnWidths.Any(w => Math.Abs(w - 110) > 0.5))
        {
            columns = new S.Columns();
            for (var c = 0; c < model.ColumnWidths.Count; c++)
            {
                var index = (uint)(c + 1);
                columns.AppendChild(new S.Column
                {
                    Min = index, Max = index,
                    Width = model.ColumnWidths[c] / 11.0,
                    CustomWidth = true
                });
            }
            worksheet.InsertBefore(columns, data);
        }
        worksheet.Save();
    }

    private static uint EnsureCellStyle(WorkbookPart wbPart, ExcelCell model)
    {
        if (!model.IsBold && !model.IsItalic && !model.IsUnderline &&
            string.IsNullOrEmpty(model.FontColor) && string.IsNullOrEmpty(model.FillColor) &&
            string.IsNullOrEmpty(model.NumberFormat) && model.Alignment == WordAlignment.Left)
            return 0;

        var stylesPart = wbPart.WorkbookStylesPart ?? wbPart.AddNewPart<WorkbookStylesPart>();
        var stylesheet = stylesPart.Stylesheet ??= CreateDefaultStylesheet();
        var fonts = stylesheet.GetFirstChild<S.Fonts>() ?? stylesheet.PrependChild(new S.Fonts());
        var fills = stylesheet.GetFirstChild<S.Fills>() ?? stylesheet.AppendChild(new S.Fills());
        var cellXfs = stylesheet.GetFirstChild<S.CellFormats>() ?? stylesheet.AppendChild(new S.CellFormats());

        var font = new S.Font();
        if (model.IsBold) font.AppendChild(new S.Bold());
        if (model.IsItalic) font.AppendChild(new S.Italic());
        if (model.IsUnderline) font.AppendChild(new S.Underline());
        if (!string.IsNullOrEmpty(model.FontColor))
            font.AppendChild(new S.Color { Rgb = "FF" + NormaliseHex(model.FontColor) });
        if (!font.ChildElements.Any()) font.AppendChild(new S.FontSize { Val = 1100 });
        fonts.AppendChild(font);
        var fontId = (uint)(fonts.ChildElements.Count - 1);

        var fillId = 0U;
        if (!string.IsNullOrEmpty(model.FillColor))
        {
            var fill = new S.Fill(new S.PatternFill
            {
                PatternType = S.PatternValues.Solid,
                ForegroundColor = new S.ForegroundColor { Rgb = "FF" + NormaliseHex(model.FillColor) },
                BackgroundColor = new S.BackgroundColor { Indexed = 64U }
            });
            fills.AppendChild(fill);
            fillId = (uint)(fills.ChildElements.Count - 1);
        }

        var numberFormatId = 0U;
        if (!string.IsNullOrEmpty(model.NumberFormat))
            numberFormatId = EnsureNumberFormat(stylesheet, model.NumberFormat);

        var xf = new S.CellFormat
        {
            FontId = fontId, FillId = fillId, NumberFormatId = numberFormatId,
            ApplyFont = true, ApplyFill = fillId > 0, ApplyNumberFormat = numberFormatId > 0,
            ApplyAlignment = model.Alignment != WordAlignment.Left
        };
        if (model.Alignment != WordAlignment.Left)
        {
            xf.AppendChild(new S.Alignment
            {
                Horizontal = model.Alignment switch
                {
                    WordAlignment.Center => S.HorizontalAlignmentValues.Center,
                    WordAlignment.Right => S.HorizontalAlignmentValues.Right,
                    WordAlignment.Justify => S.HorizontalAlignmentValues.Justify,
                    _ => S.HorizontalAlignmentValues.Left
                }
            });
        }
        cellXfs.AppendChild(xf);
        stylesPart.Stylesheet.Save();
        return (uint)(cellXfs.ChildElements.Count - 1);
    }

    private static uint EnsureNumberFormat(S.Stylesheet stylesheet, string code)
    {
        var builtin = code switch
        {
            "General" => 0U, "0" => 1U, "0.00" => 2U, "#,##0" => 3U, "#,##0.00" => 4U,
            "0%" => 9U, "0.00%" => 10U, _ => 0U
        };
        if (builtin != 0 || code == "General") return builtin;

        var formats = stylesheet.GetFirstChild<S.NumberingFormats>()
            ?? stylesheet.PrependChild(new S.NumberingFormats());
        var existing = formats.Elements<S.NumberingFormat>()
            .FirstOrDefault(n => string.Equals(n.FormatCode?.Value, code, StringComparison.OrdinalIgnoreCase));
        if (existing?.NumberFormatId?.Value is { } found) return found;
        var id = (uint)(164 + formats.ChildElements.Count);
        formats.AppendChild(new S.NumberingFormat { NumberFormatId = id, FormatCode = code });
        return id;
    }

    private static S.Stylesheet CreateDefaultStylesheet() => new(
        new S.Fonts(new S.Font(new S.FontSize { Val = 1100 })),
        new S.Fills(
            new S.Fill(new S.PatternFill { PatternType = S.PatternValues.None }),
            new S.Fill(new S.PatternFill { PatternType = S.PatternValues.Gray125 })),
        new S.Borders(new S.Border()),
        new S.CellStyleFormats(new S.CellFormat { NumberFormatId = 0, FontId = 0, FillId = 0, BorderId = 0 }),
        new S.CellFormats(new S.CellFormat { NumberFormatId = 0, FontId = 0, FillId = 0, BorderId = 0, FormatId = 0 }));

    private static string StripAlpha(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length == 8 ? value[2..] : value;
    }

    private static string NormaliseHex(string value)
    {
        var hex = value.TrimStart('#');
        if (hex.Length == 6) return hex.ToUpperInvariant();
        return hex.Length == 8 ? hex.ToUpperInvariant()[2..] : "000000";
    }

    // ---------------------------------------------------------------- New documents

    private static void CreateWord(string path)
    {
        using var word = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = word.AddMainDocumentPart();
        main.Document = new W.Document(new W.Body(
            new W.Paragraph(new W.Run(new W.Text(string.Empty))),
            new W.SectionProperties(
                new W.PageSize { Width = 12240U, Height = 15840U },
                new W.PageMargin { Top = 1440, Right = 1440, Bottom = 1440, Left = 1440 })));
        main.Document.Save();
    }

    private static void CreateExcel(string path)
    {
        using var excel = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var wbPart = excel.AddWorkbookPart();
        wbPart.Workbook = new S.Workbook(new S.Sheets(
            new S.Sheet { Name = "Sheet1", SheetId = 1U, Id = "rId1" }));
        var wsPart = wbPart.AddNewPart<WorksheetPart>("rId1");
        wsPart.Worksheet = new S.Worksheet(new S.SheetData());
        wbPart.Workbook.Save();
        wsPart.Worksheet.Save();
    }
}
