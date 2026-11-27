using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using FileHub.Office.Models;
using A = DocumentFormat.OpenXml.Drawing;
using C = DocumentFormat.OpenXml.Drawing.Charts;
using S = DocumentFormat.OpenXml.Spreadsheet;
using XDR = DocumentFormat.OpenXml.Drawing.Spreadsheet;

namespace FileHub.Office.Services;

/// <summary>
/// Reads and writes Excel charts without COM. The chart parts, drawing anchors and cell
/// references live in the xlsx package at <see cref="OfficeDocument.Path"/>; the editor only
/// keeps the materialised values in <see cref="OfficeDocument.Charts"/>.
/// </summary>
public static class ChartService
{
    private const string ChartUri = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    private const long EmuPerPixel = 9525;
    private const double PixelsPerColumn = 110.0;
    private const double PixelsPerRow = 22.0;

    private sealed record ChartEntry(
        ChartPart Part,
        DrawingsPart Drawings,
        XDR.WorksheetDrawing Root,
        XDR.GraphicFrame Frame,
        WorksheetPart Worksheet,
        string SheetName);

    // ------------------------------------------------------------------ public API

    /// <summary>Loads every chart of the workbook into <see cref="OfficeDocument.Charts"/>.</summary>
    public static void Read(OfficeDocument doc)
    {
        if (doc is null) return;
        if (string.IsNullOrWhiteSpace(doc.Path) || !File.Exists(doc.Path)) return;

        doc.Charts.Clear();
        try
        {
            using var package = SpreadsheetDocument.Open(doc.Path, false);
            var wbPart = package.WorkbookPart;
            if (wbPart is null) return;

            var index = 0;
            foreach (var entry in EnumerateCharts(wbPart))
            {
                index++;
                try
                {
                    var model = ParseChart(entry.Part.ChartSpace, doc, entry.SheetName);
                    if (model is null) continue;
                    model.PartIndex = index;
                    model.Name = FrameName(entry.Frame);
                    if (string.IsNullOrWhiteSpace(model.Name)) model.Name = model.Title;
                    ApplyAnchor(model, entry.Frame);
                    doc.Charts.Add(model);
                }
                catch
                {
                    // A single broken chart must not hide the other charts in the workbook.
                }
            }
        }
        catch
        {
            // Unreadable package: leave the model unchanged apart from the cleared list.
        }
    }

    /// <summary>Writes all charts back to the xlsx at <see cref="OfficeDocument.Path"/>.</summary>
    public static void Write(OfficeDocument doc)
    {
        if (doc is null) return;
        if (string.IsNullOrWhiteSpace(doc.Path) || !File.Exists(doc.Path)) return;

        try
        {
            using var package = SpreadsheetDocument.Open(doc.Path, true);
            var wbPart = package.WorkbookPart;
            if (wbPart is null) return;

            if (doc.Charts.Count == 0)
            {
                ClearAllCharts(wbPart);
                wbPart.Workbook.Save();
                return;
            }

            // Rewrite existing parts first: their PartIndex values refer to the order at the
            // beginning of this call, and adding a new part can shift that order.
            var entries = EnumerateCharts(wbPart).ToList();
            var processed = new HashSet<OfficeChart>();
            var pairs = new List<(OfficeChart Model, ChartPart Part)>();

            foreach (var chart in doc.Charts)
            {
                if (chart.PartIndex <= 0 || chart.PartIndex > entries.Count) continue;
                processed.Add(chart);
                var existing = entries[chart.PartIndex - 1];
                try
                {
                    var space = BuildChartSpace(chart, existing.SheetName);
                    existing.Part.ChartSpace = space;
                    space.Save();
                    UpdateAnchor(existing.Frame, chart);
                    existing.Root.Save();
                    pairs.Add((chart, existing.Part));
                }
                catch
                {
                    // The old part stays in place; do not create a duplicate for this chart.
                }
            }

            foreach (var chart in doc.Charts)
            {
                if (processed.Contains(chart)) continue;
                try
                {
                    var sheetName = string.IsNullOrWhiteSpace(chart.SheetName)
                        ? FirstSheetName(wbPart)
                        : chart.SheetName;
                    var part = AddNewChart(wbPart, chart, sheetName);
                    if (part is null) continue;
                    processed.Add(chart);
                    pairs.Add((chart, part));
                }
                catch
                {
                    // Keep writing the remaining charts if one of them is malformed.
                }
            }

            // Refresh every PartIndex from the final package order so a second Write reuses
            // the parts instead of creating duplicates.
            var current = EnumerateCharts(wbPart).ToList();
            foreach (var (model, part) in pairs)
            {
                var index = current.FindIndex(e => ReferenceEquals(e.Part, part));
                if (index >= 0) model.PartIndex = index + 1;
            }
            foreach (var chart in doc.Charts)
                if (!processed.Contains(chart)) chart.PartIndex = 0;

            wbPart.Workbook.Save();
        }
        catch
        {
            // The file may be locked; the in-memory model is still intact.
        }
    }

    /// <summary>Builds a chart from an A1 range: column one holds categories, the rest hold series.</summary>
    public static OfficeChart CreateFromRange(OfficeDocument doc, string sheetName, string range,
        OfficeChartType type, string name)
    {
        var chart = new OfficeChart
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Chart" : name,
            Title = name ?? string.Empty,
            Type = type,
            SheetName = sheetName ?? string.Empty
        };

        if (!TryParseRange(range, out var parsedSheet, out var row1, out var col1, out var row2, out var col2))
            return chart;
        if (string.IsNullOrWhiteSpace(chart.SheetName)) chart.SheetName = parsedSheet;

        var sheet = FindSheet(doc, chart.SheetName) ?? FindSheet(doc, parsedSheet);
        if (sheet is null) return chart;

        NormalizeRange(ref row1, ref col1, ref row2, ref col2);
        if (row2 <= row1 || col2 <= col1) return chart;

        for (var row = row1 + 1; row <= row2; row++)
            chart.Categories.Add(sheet.Get(row, col1)?.Display ?? string.Empty);

        for (var col = col1 + 1; col <= col2; col++)
        {
            var series = new ChartSeries
            {
                Name = sheet.Get(row1, col)?.Display ?? "Series " + (col - col1),
                ValueRange = string.Create(CultureInfo.InvariantCulture,
                    $"'{EscapeSheetName(chart.SheetName)}'!{Abs(col, row1 + 1)}:{Abs(col, row2)}")
            };
            for (var row = row1 + 1; row <= row2; row++)
            {
                var text = sheet.Get(row, col)?.Display ?? string.Empty;
                series.Values.Add(double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
                    ? value : 0);
            }
            chart.Series.Add(series);
        }

        chart.AnchorCell = ExcelSheet.CellReference(2, Math.Max(8, col2 + 2));
        return chart;
    }

    /// <summary>Removes a chart model and its chart part / graphic frame from the package.</summary>
    public static void Delete(OfficeDocument doc, OfficeChart chart)
    {
        if (doc is null || chart is null) return;
        var removedIndex = chart.PartIndex;
        doc.Charts.Remove(chart);
        if (removedIndex > 0)
            foreach (var remaining in doc.Charts)
                if (remaining.PartIndex > removedIndex) remaining.PartIndex--;
        if (string.IsNullOrWhiteSpace(doc.Path) || !File.Exists(doc.Path)) return;

        try
        {
            using var package = SpreadsheetDocument.Open(doc.Path, true);
            var wbPart = package.WorkbookPart;
            if (wbPart is null) return;
            var entries = EnumerateCharts(wbPart).ToList();

            ChartEntry? target = null;
            if (chart.PartIndex > 0 && chart.PartIndex <= entries.Count)
                target = entries[chart.PartIndex - 1];
            if (target is null && !string.IsNullOrWhiteSpace(chart.Name))
                target = entries.FirstOrDefault(e => string.Equals(FrameName(e.Frame), chart.Name,
                    StringComparison.OrdinalIgnoreCase));
            if (target is null) return;
            RemoveChartEntry(target);
        }
        catch
        {
            // Nothing else to do: the model no longer references the chart.
        }
    }

    // ------------------------------------------------------------------ reading

    private static IEnumerable<ChartEntry> EnumerateCharts(WorkbookPart wbPart)
    {
        var sheets = wbPart.Workbook?.Sheets?.Elements<S.Sheet>() ?? Enumerable.Empty<S.Sheet>();
        foreach (var sheet in sheets)
        {
            if (sheet.Id?.Value is not { } sheetRid) continue;
            if (wbPart.GetPartById(sheetRid) is not WorksheetPart wsPart) continue;
            var drawings = wsPart.DrawingsPart;
            var root = drawings?.WorksheetDrawing;
            if (drawings is null || root is null) continue;

            foreach (var frame in root.Descendants<XDR.GraphicFrame>())
            {
                var reference = frame.Descendants<C.ChartReference>().FirstOrDefault();
                if (reference?.Id?.Value is not { } chartRid) continue;
                if (drawings.GetPartById(chartRid) is not ChartPart chartPart) continue;
                yield return new ChartEntry(chartPart, drawings, root, frame, wsPart,
                    sheet.Name?.Value ?? string.Empty);
            }
        }
    }

    private static OfficeChart? ParseChart(C.ChartSpace? space, OfficeDocument doc, string sheetName)
    {
        var chart = space?.GetFirstChild<C.Chart>();
        var plot = chart?.GetFirstChild<C.PlotArea>();
        if (chart is null || plot is null) return null;

        var model = new OfficeChart
        {
            SheetName = sheetName,
            Title = ReadTitle(chart, doc, sheetName)
        };

        foreach (var group in plot.ChildElements)
        {
            switch (group)
            {
                case C.BarChart bar:
                    model.Type = bar.BarDirection?.Val?.Value == C.BarDirectionValues.Bar
                        ? OfficeChartType.Bar : OfficeChartType.Column;
                    ReadSeries(bar.Elements<C.BarChartSeries>(), model, doc, sheetName);
                    break;
                case C.LineChart line:
                    model.Type = OfficeChartType.Line;
                    ReadSeries(line.Elements<C.LineChartSeries>(), model, doc, sheetName);
                    break;
                case C.PieChart pie:
                    model.Type = OfficeChartType.Pie;
                    ReadSeries(pie.Elements<C.PieChartSeries>(), model, doc, sheetName);
                    break;
                case C.AreaChart area:
                    model.Type = OfficeChartType.Area;
                    ReadSeries(area.Elements<C.AreaChartSeries>(), model, doc, sheetName);
                    break;
            }
        }
        return model;
    }

    private static string ReadTitle(C.Chart chart, OfficeDocument doc, string sheetName)
    {
        var text = chart.Title?.ChartText;
        if (text?.RichText is { } rich)
            return string.Concat(rich.Descendants<A.Text>().Select(t => t.Text ?? string.Empty));
        if (text?.StringReference is { } reference)
        {
            var values = ResolveStrings(reference.Formula?.Text, doc, sheetName);
            if (values.Count > 0) return values[0];
            var cache = CacheStrings(reference.StringCache);
            if (cache.Count > 0) return cache[0];
        }
        if (text?.StringLiteral is { } literal)
            return string.Concat(literal.Descendants<A.Text>().Select(t => t.Text ?? string.Empty));
        return string.Empty;
    }

    private static void ReadSeries(IEnumerable<OpenXmlElement> elements, OfficeChart model,
        OfficeDocument doc, string fallbackSheet)
    {
        foreach (var element in elements)
        {
            var series = new ChartSeries();
            var text = element.GetFirstChild<C.SeriesText>();
            if (text?.StringReference is { } nameRef)
            {
                var names = ResolveStrings(nameRef.Formula?.Text, doc, fallbackSheet);
                series.Name = names.Count > 0
                    ? names[0]
                    : CacheStrings(nameRef.StringCache).FirstOrDefault() ?? string.Empty;
            }
            else if (text?.NumericValue is { } literal)
            {
                series.Name = literal.Text ?? string.Empty;
            }

            if (model.Categories.Count == 0)
            {
                var category = element.ChildElements.OfType<C.CategoryAxisData>().FirstOrDefault();
                if (category?.ChildElements.OfType<C.StringReference>().FirstOrDefault() is { } strRef)
                {
                    var values = ResolveStrings(strRef.Formula?.Text, doc, fallbackSheet);
                    if (values.Count == 0) values = CacheStrings(strRef.StringCache);
                    foreach (var value in values) model.Categories.Add(value);
                }
                else if (category?.ChildElements.OfType<C.NumberReference>().FirstOrDefault() is { } numRef)
                {
                    var values = ResolveNumbers(numRef.Formula?.Text, doc, fallbackSheet);
                    if (values.Count == 0) values = CacheNumbers(numRef.NumberingCache);
                    foreach (var value in values)
                        model.Categories.Add(value.ToString(CultureInfo.InvariantCulture));
                }
            }

            var valuesElement = element.ChildElements.OfType<C.Values>().FirstOrDefault();
            if (valuesElement?.ChildElements.OfType<C.NumberReference>().FirstOrDefault() is { } valueRef)
            {
                series.ValueRange = valueRef.Formula?.Text ?? string.Empty;
                var values = ResolveNumbers(valueRef.Formula?.Text, doc, fallbackSheet);
                if (values.Count == 0) values = CacheNumbers(valueRef.NumberingCache);
                foreach (var value in values) series.Values.Add(value);
            }
            model.Series.Add(series);
        }
    }

    private static List<string> ResolveStrings(string? formula, OfficeDocument doc, string fallbackSheet)
    {
        var result = new List<string>();
        if (!TryParseRange(formula, out var sheet, out var row1, out var col1, out var row2, out var col2))
            return result;
        var target = FindSheet(doc, sheet) ?? FindSheet(doc, fallbackSheet);
        if (target is null || !SafeRange(row1, col1, row2, col2)) return result;
        NormalizeRange(ref row1, ref col1, ref row2, ref col2);
        for (var row = row1; row <= row2; row++)
            for (var col = col1; col <= col2; col++)
                result.Add(target.Get(row, col)?.Display ?? string.Empty);
        return result;
    }

    private static List<double> ResolveNumbers(string? formula, OfficeDocument doc, string fallbackSheet)
    {
        var result = new List<double>();
        if (!TryParseRange(formula, out var sheet, out var row1, out var col1, out var row2, out var col2))
            return result;
        var target = FindSheet(doc, sheet) ?? FindSheet(doc, fallbackSheet);
        if (target is null || !SafeRange(row1, col1, row2, col2)) return result;
        NormalizeRange(ref row1, ref col1, ref row2, ref col2);
        for (var row = row1; row <= row2; row++)
            for (var col = col1; col <= col2; col++)
            {
                var text = target.Get(row, col)?.Display ?? string.Empty;
                result.Add(double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
                    ? value : 0);
            }
        return result;
    }

    private static List<string> CacheStrings(C.StringCache? cache) =>
        cache?.Elements<C.StringPoint>()
            .Select(p => p.NumericValue?.Text ?? string.Empty).ToList() ?? new List<string>();

    private static List<double> CacheNumbers(C.NumberingCache? cache)
    {
        var result = new List<double>();
        if (cache is null) return result;
        foreach (var point in cache.Elements<C.NumericPoint>())
        {
            var text = point.NumericValue?.Text ?? string.Empty;
            result.Add(double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
                ? value : 0);
        }
        return result;
    }

    private static void ApplyAnchor(OfficeChart chart, XDR.GraphicFrame frame)
    {
        switch (frame.Parent)
        {
            case XDR.TwoCellAnchor two:
                ReadMarker(two.FromMarker, out var row, out var col);
                ReadMarker(two.ToMarker, out var endRow, out var endCol);
                chart.AnchorCell = ExcelSheet.CellReference(Math.Max(1, row + 1), Math.Max(1, col + 1));
                chart.WidthPx = (int)Math.Max(1, (endCol - col) * PixelsPerColumn +
                    (ReadOffset(two.ToMarker?.ColumnOffset) - ReadOffset(two.FromMarker?.ColumnOffset)) / EmuPerPixel);
                chart.HeightPx = (int)Math.Max(1, (endRow - row) * PixelsPerRow +
                    (ReadOffset(two.ToMarker?.RowOffset) - ReadOffset(two.FromMarker?.RowOffset)) / EmuPerPixel);
                break;
            case XDR.OneCellAnchor one:
                ReadMarker(one.FromMarker, out row, out col);
                chart.AnchorCell = ExcelSheet.CellReference(Math.Max(1, row + 1), Math.Max(1, col + 1));
                chart.WidthPx = (int)Math.Max(1, (one.Extent?.Cx ?? 0) / EmuPerPixel);
                chart.HeightPx = (int)Math.Max(1, (one.Extent?.Cy ?? 0) / EmuPerPixel);
                break;
        }
    }

    private static void ReadMarker(XDR.MarkerType? marker, out int row, out int col)
    {
        col = int.TryParse(marker?.ColumnId?.Text, out var c) ? c : 0;
        row = int.TryParse(marker?.RowId?.Text, out var r) ? r : 0;
    }

    private static double ReadOffset(XDR.ColumnOffset? column) =>
        double.TryParse(column?.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static double ReadOffset(XDR.RowOffset? row) =>
        double.TryParse(row?.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : 0;

    // ------------------------------------------------------------------ writing

    private static ChartPart? AddNewChart(WorkbookPart wbPart, OfficeChart chart, string sheetName)
    {
        var wsPart = FindWorksheetPart(wbPart, sheetName);
        if (wsPart is null) return null;

        var drawings = wsPart.DrawingsPart ?? wsPart.AddNewPart<DrawingsPart>();
        var root = drawings.WorksheetDrawing ?? new XDR.WorksheetDrawing();
        if (drawings.WorksheetDrawing is null) drawings.WorksheetDrawing = root;

        var chartPart = drawings.AddNewPart<ChartPart>();
        var space = BuildChartSpace(chart, sheetName);
        chartPart.ChartSpace = space;
        space.Save();

        var (row, col) = ExcelSheet.ParseReference(chart.AnchorCell);
        if (row < 1 || col < 1) { row = 2; col = 8; }
        var endCol = col - 1 + Math.Max(1, (int)Math.Ceiling(chart.WidthPx / PixelsPerColumn));
        var endRow = row - 1 + Math.Max(1, (int)Math.Ceiling(chart.HeightPx / PixelsPerRow));

        uint id = 1;
        foreach (var property in root.Descendants<XDR.NonVisualDrawingProperties>())
            id = Math.Max(id, (property.Id?.Value ?? 0) + 1);

        var frame = new XDR.GraphicFrame(
            new XDR.NonVisualGraphicFrameProperties(
                new XDR.NonVisualDrawingProperties
                {
                    Id = id,
                    Name = string.IsNullOrWhiteSpace(chart.Name) ? "Chart" : chart.Name
                },
                new XDR.NonVisualGraphicFrameDrawingProperties()),
            new XDR.Transform(
                new A.Offset { X = 0L, Y = 0L },
                new A.Extents
                {
                    Cx = Math.Max(1, chart.WidthPx) * EmuPerPixel,
                    Cy = Math.Max(1, chart.HeightPx) * EmuPerPixel
                }),
            new A.Graphic(new A.GraphicData(new C.ChartReference { Id = drawings.GetIdOfPart(chartPart) })
            {
                Uri = ChartUri
            }));

        var anchor = new XDR.TwoCellAnchor(
            MakeFromMarker(row - 1, col - 1),
            MakeToMarker(endRow, endCol),
            frame,
            new XDR.ClientData());
        root.AppendChild(anchor);
        root.Save();

        var worksheet = wsPart.Worksheet ?? (wsPart.Worksheet = new S.Worksheet(new S.SheetData()));
        if (worksheet.GetFirstChild<S.Drawing>() is null)
        {
            var drawing = new S.Drawing { Id = wsPart.GetIdOfPart(drawings) };
            var before = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "legacyDrawing", "legacyDrawingHF", "drawingHF", "picture", "oleObjects",
                "controls", "webPublishItems", "tableParts", "extLst"
            };
            var marker = worksheet.ChildElements.FirstOrDefault(e => before.Contains(e.LocalName));
            if (marker is null) worksheet.AppendChild(drawing);
            else worksheet.InsertBefore(drawing, marker);
        }
        worksheet.Save();
        return chartPart;
    }

    private static void ClearAllCharts(WorkbookPart wbPart)
    {
        foreach (var entry in EnumerateCharts(wbPart).ToList())
        {
            try { RemoveChartEntry(entry); }
            catch { /* keep removing the remaining charts */ }
        }
        foreach (var ws in wbPart.WorksheetParts.ToList())
        {
            var drawings = ws.DrawingsPart;
            if (drawings is null) continue;
            try
            {
                drawings.WorksheetDrawing?.Save();
                if (drawings.WorksheetDrawing is { } root && root.ChildElements.Any()) continue;
                var element = ws.Worksheet?.GetFirstChild<S.Drawing>();
                element?.Remove();
                ws.Worksheet?.Save();
                ws.DeletePart(drawings);
            }
            catch { /* an unrelated drawing may still be in use */ }
        }
    }

    private static void RemoveChartEntry(ChartEntry entry)
    {
        var parent = entry.Frame.Parent;
        entry.Frame.Remove();
        if (parent is XDR.TwoCellAnchor or XDR.OneCellAnchor or XDR.AbsoluteAnchor &&
            !parent.ChildElements.OfType<XDR.GraphicFrame>().Any())
            parent.Remove();

        entry.Drawings.DeletePart(entry.Part);
        entry.Root.Save();

        if (entry.Root.ChildElements.Any()) return;
        var drawingElement = entry.Worksheet.Worksheet?.GetFirstChild<S.Drawing>();
        drawingElement?.Remove();
        entry.Worksheet.Worksheet?.Save();
        entry.Worksheet.DeletePart(entry.Drawings);
    }

    private static void UpdateAnchor(XDR.GraphicFrame frame, OfficeChart chart)
    {
        if (frame.Parent is not XDR.TwoCellAnchor anchor) return;
        var (row, col) = ExcelSheet.ParseReference(chart.AnchorCell);
        if (row < 1 || col < 1) return;
        WriteMarker(anchor.FromMarker, row - 1, col - 1);
        WriteMarker(anchor.ToMarker,
            row - 1 + Math.Max(1, (int)Math.Ceiling(chart.HeightPx / PixelsPerRow)),
            col - 1 + Math.Max(1, (int)Math.Ceiling(chart.WidthPx / PixelsPerColumn)));
    }

    private static XDR.FromMarker MakeFromMarker(int row, int col) => new(
        new XDR.ColumnId { Text = col.ToString(CultureInfo.InvariantCulture) },
        new XDR.ColumnOffset { Text = "0" },
        new XDR.RowId { Text = row.ToString(CultureInfo.InvariantCulture) },
        new XDR.RowOffset { Text = "0" });

    private static XDR.ToMarker MakeToMarker(int row, int col) => new(
        new XDR.ColumnId { Text = col.ToString(CultureInfo.InvariantCulture) },
        new XDR.ColumnOffset { Text = "0" },
        new XDR.RowId { Text = row.ToString(CultureInfo.InvariantCulture) },
        new XDR.RowOffset { Text = "0" });

    private static void WriteMarker(XDR.MarkerType? marker, int row, int col)
    {
        if (marker is null) return;
        if (marker.ColumnId is not null) marker.ColumnId.Text = col.ToString(CultureInfo.InvariantCulture);
        if (marker.RowId is not null) marker.RowId.Text = row.ToString(CultureInfo.InvariantCulture);
        if (marker.ColumnOffset is not null) marker.ColumnOffset.Text = "0";
        if (marker.RowOffset is not null) marker.RowOffset.Text = "0";
    }

    private static C.ChartSpace BuildChartSpace(OfficeChart chart, string sheetName)
    {
        var plot = new C.PlotArea(new C.Layout());
        var group = CreateChartGroup(chart, sheetName);
        if (group is not null) plot.AppendChild(group);
        plot.AppendChild(CreateCategoryAxis());
        plot.AppendChild(CreateValueAxis());

        var element = new C.Chart
        {
            PlotArea = plot,
            Legend = new C.Legend(new C.LegendPosition { Val = C.LegendPositionValues.Right }, new C.Layout())
        };
        if (!string.IsNullOrWhiteSpace(chart.Title))
        {
            var rich = new C.RichText(
                new A.BodyProperties(),
                new A.ListStyle(),
                new A.Paragraph(new A.Run(new A.Text { Text = chart.Title })));
            element.Title = new C.Title(new C.ChartText(rich), new C.Layout());
        }

        var space = new C.ChartSpace();
        space.AppendChild(element);
        return space;
    }

    private static OpenXmlElement? CreateChartGroup(OfficeChart chart, string sheetName)
    {
        var count = Math.Max(1, chart.Categories.Count);
        var categoryFormula = string.Create(CultureInfo.InvariantCulture,
            $"'{EscapeSheetName(sheetName)}'!{Abs(1, 2)}:{Abs(1, count + 1)}");

        var series = new List<OpenXmlElement>();
        for (var i = 0; i < chart.Series.Count; i++)
        {
            var model = chart.Series[i];
            var valueFormula = string.IsNullOrWhiteSpace(model.ValueRange)
                ? string.Create(CultureInfo.InvariantCulture,
                    $"'{EscapeSheetName(sheetName)}'!{Abs(i + 2, 2)}:{Abs(i + 2, count + 1)}")
                : model.ValueRange;
            var nameFormula = string.Create(CultureInfo.InvariantCulture,
                $"'{EscapeSheetName(sheetName)}'!{Abs(i + 2, 1)}");

            var text = new C.SeriesText(new C.StringReference(
                new C.Formula(nameFormula),
                MakeStringCache(new[] { model.Name })));
            var category = new C.CategoryAxisData(new C.StringReference(
                new C.Formula(categoryFormula),
                MakeStringCache(chart.Categories)));
            var values = new C.Values(new C.NumberReference(
                new C.Formula(valueFormula),
                MakeNumberCache(model.Values)));

            OpenXmlElement item = chart.Type switch
            {
                OfficeChartType.Line => new C.LineChartSeries(
                    new C.Index { Val = (uint)i }, new C.Order { Val = (uint)i }, text, category, values),
                OfficeChartType.Pie => new C.PieChartSeries(
                    new C.Index { Val = (uint)i }, new C.Order { Val = (uint)i }, text, category, values),
                OfficeChartType.Area => new C.AreaChartSeries(
                    new C.Index { Val = (uint)i }, new C.Order { Val = (uint)i }, text, category, values),
                _ => new C.BarChartSeries(
                    new C.Index { Val = (uint)i }, new C.Order { Val = (uint)i }, text, category, values)
            };
            series.Add(item);
        }

        switch (chart.Type)
        {
            case OfficeChartType.Line:
            {
                var children = new List<OpenXmlElement> { new C.Grouping { Val = C.GroupingValues.Standard } };
                children.AddRange(series);
                return new C.LineChart(children);
            }
            case OfficeChartType.Pie:
                return new C.PieChart(series);
            case OfficeChartType.Area:
            {
                var children = new List<OpenXmlElement> { new C.Grouping { Val = C.GroupingValues.Standard } };
                children.AddRange(series);
                return new C.AreaChart(children);
            }
            case OfficeChartType.Bar:
            {
                var children = new List<OpenXmlElement>
                {
                    new C.BarDirection { Val = C.BarDirectionValues.Bar },
                    new C.BarGrouping { Val = C.BarGroupingValues.Clustered }
                };
                children.AddRange(series);
                return new C.BarChart(children);
            }
            default:
            {
                var children = new List<OpenXmlElement>
                {
                    new C.BarDirection { Val = C.BarDirectionValues.Column },
                    new C.BarGrouping { Val = C.BarGroupingValues.Clustered }
                };
                children.AddRange(series);
                return new C.BarChart(children);
            }
        }
    }

    private static C.CategoryAxis CreateCategoryAxis() => new(
        new C.AxisId { Val = 10U },
        new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }),
        new C.Delete { Val = false },
        new C.AxisPosition { Val = C.AxisPositionValues.Bottom },
        new C.TickLabelPosition { Val = C.TickLabelPositionValues.NextTo },
        new C.CrossingAxis { Val = 100U },
        new C.Crosses { Val = C.CrossesValues.AutoZero });

    private static C.ValueAxis CreateValueAxis() => new(
        new C.AxisId { Val = 100U },
        new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }),
        new C.Delete { Val = false },
        new C.AxisPosition { Val = C.AxisPositionValues.Left },
        new C.TickLabelPosition { Val = C.TickLabelPositionValues.NextTo },
        new C.CrossingAxis { Val = 10U },
        new C.Crosses { Val = C.CrossesValues.AutoZero });

    private static C.StringCache MakeStringCache(IEnumerable<string> values)
    {
        var list = values.ToList();
        var cache = new C.StringCache { PointCount = new C.PointCount { Val = (uint)list.Count } };
        for (var i = 0; i < list.Count; i++)
            cache.AppendChild(new C.StringPoint
            {
                Index = (uint)i,
                NumericValue = new C.NumericValue(list[i] ?? string.Empty)
            });
        return cache;
    }

    private static C.NumberingCache MakeNumberCache(IEnumerable<double> values)
    {
        var list = values.ToList();
        var cache = new C.NumberingCache();
        cache.AppendChild(new C.PointCount { Val = (uint)list.Count });
        for (var i = 0; i < list.Count; i++)
            cache.AppendChild(new C.NumericPoint
            {
                Index = (uint)i,
                NumericValue = new C.NumericValue(list[i].ToString("R", CultureInfo.InvariantCulture))
            });
        return cache;
    }

    // ------------------------------------------------------------------ helpers

    private static bool TryParseRange(string? formula, out string sheet, out int row1, out int col1,
        out int row2, out int col2)
    {
        sheet = string.Empty;
        row1 = col1 = row2 = col2 = 0;
        if (string.IsNullOrWhiteSpace(formula)) return false;

        var text = formula.Trim().TrimStart('=').Trim();
        var bang = text.IndexOf('!');
        if (bang >= 0)
        {
            sheet = text[..bang].Trim();
            if (sheet.StartsWith('\'')) sheet = sheet.Trim('\'');
            var bracket = sheet.IndexOf(']');
            if (bracket >= 0) sheet = sheet[(bracket + 1)..];
            sheet = sheet.Replace("''", "'", StringComparison.Ordinal);
            text = text[(bang + 1)..].Trim();
        }
        text = text.Replace("$", string.Empty, StringComparison.Ordinal).Trim();
        if (text.Length == 0) return false;

        var colon = text.IndexOf(':');
        var first = colon >= 0 ? text[..colon] : text;
        var second = colon >= 0 ? text[(colon + 1)..] : text;
        (row1, col1) = ExcelSheet.ParseReference(first);
        (row2, col2) = ExcelSheet.ParseReference(second);
        return row1 > 0 && col1 > 0 && row2 > 0 && col2 > 0;
    }

    private static void NormalizeRange(ref int row1, ref int col1, ref int row2, ref int col2)
    {
        if (row1 > row2) (row1, row2) = (row2, row1);
        if (col1 > col2) (col1, col2) = (col2, col1);
    }

    private static bool SafeRange(int row1, int col1, int row2, int col2) =>
        row1 is > 0 and <= 100000 && col1 is > 0 and <= 1000 &&
        row2 is > 0 and <= 100000 && col2 is > 0 and <= 1000 &&
        (long)(row2 - row1) * (col2 - col1) < 1000000;

    private static ExcelSheet? FindSheet(OfficeDocument doc, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return doc.Sheets.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static WorksheetPart? FindWorksheetPart(WorkbookPart wbPart, string? name)
    {
        var sheets = wbPart.Workbook?.Sheets?.Elements<S.Sheet>() ?? Enumerable.Empty<S.Sheet>();
        var sheet = sheets.FirstOrDefault(s => string.Equals(s.Name?.Value, name, StringComparison.OrdinalIgnoreCase))
                    ?? sheets.FirstOrDefault();
        if (sheet?.Id?.Value is not { } rid) return null;
        return wbPart.GetPartById(rid) as WorksheetPart;
    }

    private static string FirstSheetName(WorkbookPart wbPart) =>
        wbPart.Workbook?.Sheets?.Elements<S.Sheet>().FirstOrDefault()?.Name?.Value ?? "Sheet1";

    private static string FrameName(XDR.GraphicFrame frame) =>
        frame.NonVisualGraphicFrameProperties?.NonVisualDrawingProperties?.Name?.Value ?? string.Empty;

    private static string EscapeSheetName(string name) => name.Replace("'", "''", StringComparison.Ordinal);

    private static string Abs(int col, int row) =>
        "$" + ColumnLetters(col) + "$" + row.ToString(CultureInfo.InvariantCulture);

    private static string ColumnLetters(int col)
    {
        var text = string.Empty;
        while (col > 0)
        {
            var rem = (col - 1) % 26;
            text = (char)('A' + rem) + text;
            col = (col - 1) / 26;
        }
        return text.Length == 0 ? "A" : text;
    }
}
