using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using FileHub.Office.Models;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace FileHub.Office.Services;

/// <summary>
/// Reads and writes Excel pivot tables. The public surface works on the normalised
/// <see cref="OfficeDocument"/> model; the Open XML package at <see cref="OfficeDocument.Path"/>
/// is opened on demand so the service can be used independently of the load/save pipeline.
/// </summary>
public static class PivotService
{
    private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string GrandTotal = "Grand Total";

    // ------------------------------------------------------------------ public API

    public static void Read(OfficeDocument doc)
    {
        if (doc is null || string.IsNullOrWhiteSpace(doc.Path) || doc.Kind != OfficeDocumentKind.Excel)
            return;

        doc.PivotTables.Clear();
        try
        {
            using var package = SpreadsheetDocument.Open(doc.Path, false);
            var wbPart = package.WorkbookPart;
            if (wbPart is null) return;

            var index = 0;
            foreach (var wsPart in wbPart.WorksheetParts)
            {
                foreach (var pivotPart in wsPart.PivotTableParts)
                {
                    try
                    {
                        var model = ReadPivot(doc, wbPart, pivotPart, index);
                        if (model is not null)
                        {
                            doc.PivotTables.Add(model);
                            index++;
                        }
                    }
                    catch
                    {
                        // A malformed pivot must not prevent the remaining ones from loading.
                    }
                }
            }
        }
        catch
        {
            // Nothing to read from an unreadable package.
        }
    }

    public static void Write(OfficeDocument doc)
    {
        if (doc is null || string.IsNullOrWhiteSpace(doc.Path) || doc.Kind != OfficeDocumentKind.Excel)
            return;
        if (doc.PivotTables.Count == 0) return;

        try
        {
            using var package = SpreadsheetDocument.Open(doc.Path, true);
            var wbPart = package.WorkbookPart;
            if (wbPart is null) return;

            var nextCacheId = NextCacheId(wbPart);
            foreach (var pivot in doc.PivotTables)
            {
                try
                {
                    WritePivot(doc, wbPart, pivot, nextCacheId);
                    nextCacheId++;
                }
                catch
                {
                    // Keep writing the other pivots even if one cannot be materialised.
                }
            }

            wbPart.Workbook.Save();
            foreach (var wsPart in wbPart.WorksheetParts)
                if (wsPart.Worksheet is not null) wsPart.Worksheet.Save();
        }
        catch
        {
            // Writing is best-effort; the model in memory stays authoritative.
        }
    }

    public static PivotTableModel CreateFromRange(OfficeDocument doc, string sheetName, string range,
        string rowField, string columnField, string valueField, PivotAggregation aggregation)
    {
        var model = new PivotTableModel
        {
            Name = string.IsNullOrWhiteSpace(valueField) ? "Pivot" : $"{aggregation} of {valueField}",
            SourceSheet = sheetName ?? string.Empty,
            SourceRange = range ?? string.Empty,
            RowField = rowField ?? string.Empty,
            ColumnField = columnField ?? string.Empty,
            ValueField = valueField ?? string.Empty,
            Aggregation = aggregation
        };

        try
        {
            if (doc is null || !TryParseRange(range, out _, out _, out _, out _)) return model;
            var sheet = FindSheet(doc, sheetName);
            if (sheet is null) return model;
            RecalculateCore(model, sheet);
        }
        catch
        {
            model.Grid.Clear();
        }
        return model;
    }

    public static void Recalculate(PivotTableModel pivot, OfficeDocument doc)
    {
        if (pivot is null || doc is null) return;
        pivot.Grid.Clear();
        try
        {
            var sheet = FindSheet(doc, pivot.SourceSheet) ?? doc.Sheets.FirstOrDefault();
            if (sheet is not null) RecalculateCore(pivot, sheet);
        }
        catch
        {
            pivot.Grid.Clear();
        }
    }

    // ------------------------------------------------------------------ read

    private static PivotTableModel? ReadPivot(OfficeDocument doc, WorkbookPart wbPart, PivotTablePart pivotPart, int index)
    {
        var definition = pivotPart.PivotTableDefinition;
        var cachePart = pivotPart.PivotTableCacheDefinitionPart;
        if (definition is null || cachePart is null) return null;
        var cache = cachePart.PivotCacheDefinition;
        if (cache is null) return null;

        var cacheFields = cache.CacheFields?.Elements<S.CacheField>().ToList() ?? new List<S.CacheField>();
        var names = cacheFields.Select((f, i) =>
        {
            var name = f.Name?.Value ?? string.Empty;
            return string.IsNullOrWhiteSpace(name) ? "Field" + (i + 1).ToString(CultureInfo.InvariantCulture) : name;
        }).ToList();

        SplitSource(cache.CacheSource?.WorksheetSource?.Reference?.Value,
            out var referenceSheet, out var referenceRange);
        var sheetName = cache.CacheSource?.WorksheetSource?.Sheet?.Value;
        if (string.IsNullOrWhiteSpace(sheetName)) sheetName = referenceSheet;

        var rowIndex = FirstFieldIndex(definition.RowFields);
        var columnIndex = FirstFieldIndex(definition.ColumnFields);
        var dataField = definition.DataFields?.Elements<S.DataField>().FirstOrDefault();
        var valueIndex = dataField is null ? -1 : (int)(dataField.Field?.Value ?? 0U);

        var model = new PivotTableModel
        {
            Name = definition.Name?.Value ?? "Pivot" + (index + 1).ToString(CultureInfo.InvariantCulture),
            SourceSheet = sheetName ?? string.Empty,
            SourceRange = referenceRange,
            RowField = FieldName(names, rowIndex),
            ColumnField = FieldName(names, columnIndex),
            ValueField = FieldName(names, valueIndex),
            Aggregation = ToAggregation(dataField?.Subtotal?.Value),
            PartIndex = index
        };
        foreach (var name in names) model.Fields.Add(name);

        // Prefer the in-memory sheets; fall back to the worksheet inside the package.
        var sheet = FindSheet(doc, model.SourceSheet) ?? SheetFromPackage(wbPart, model.SourceSheet);
        if (sheet is not null) RecalculateCore(model, sheet);
        return model;
    }

    private static string FieldName(IReadOnlyList<string> names, int index) =>
        index >= 0 && index < names.Count ? names[index] : string.Empty;

    private static int FirstFieldIndex(S.RowFields? rowFields) => FirstFieldIndex((OpenXmlElement?)rowFields);

    private static int FirstFieldIndex(S.ColumnFields? columnFields) => FirstFieldIndex((OpenXmlElement?)columnFields);

    private static int FirstFieldIndex(OpenXmlElement? container)
    {
        if (container is null) return -1;
        foreach (var child in container.ChildElements)
        {
            if (!string.Equals(child.LocalName, "fieldIndex", StringComparison.OrdinalIgnoreCase)) continue;
            var raw = child.GetAttribute("x", string.Empty).Value;
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return value;
        }
        return -1;
    }

    private static PivotAggregation ToAggregation(S.DataConsolidateFunctionValues? subtotal)
    {
        if (subtotal is null) return PivotAggregation.Sum;
        if (subtotal == S.DataConsolidateFunctionValues.Average) return PivotAggregation.Average;
        if (subtotal == S.DataConsolidateFunctionValues.Count ||
            subtotal == S.DataConsolidateFunctionValues.CountNumbers) return PivotAggregation.Count;
        if (subtotal == S.DataConsolidateFunctionValues.Minimum) return PivotAggregation.Min;
        if (subtotal == S.DataConsolidateFunctionValues.Maximum) return PivotAggregation.Max;
        return PivotAggregation.Sum;
    }

    private static S.DataConsolidateFunctionValues FromAggregation(PivotAggregation aggregation) => aggregation switch
    {
        PivotAggregation.Count => S.DataConsolidateFunctionValues.Count,
        PivotAggregation.Average => S.DataConsolidateFunctionValues.Average,
        PivotAggregation.Min => S.DataConsolidateFunctionValues.Minimum,
        PivotAggregation.Max => S.DataConsolidateFunctionValues.Maximum,
        _ => S.DataConsolidateFunctionValues.Sum
    };

    // ------------------------------------------------------------------ write

    private static uint NextCacheId(WorkbookPart wbPart)
    {
        var caches = wbPart.Workbook.GetFirstChild<S.PivotCaches>();
        var max = caches?.Elements<S.PivotCache>().Select(c => c.CacheId?.Value ?? 0U).DefaultIfEmpty(0U).Max() ?? 0U;
        return max + 1;
    }

    private static void WritePivot(OfficeDocument doc, WorkbookPart wbPart, PivotTableModel pivot, uint cacheId)
    {
        var name = string.IsNullOrWhiteSpace(pivot.Name)
            ? "Pivot" + (pivot.PartIndex + 1).ToString(CultureInfo.InvariantCulture)
            : pivot.Name;

        var sheetElement = wbPart.Workbook.Sheets?.Elements<S.Sheet>().FirstOrDefault(s =>
            string.Equals(s.Name?.Value, pivot.SourceSheet, StringComparison.OrdinalIgnoreCase));
        var target = sheetElement?.Id?.Value is { } id && wbPart.GetPartById(id) is WorksheetPart found
            ? found
            : wbPart.WorksheetParts.FirstOrDefault();
        if (target is null || target.Worksheet is null) return;

        var existingPart = target.PivotTableParts.FirstOrDefault(p =>
            string.Equals(p.PivotTableDefinition?.Name?.Value, name, StringComparison.OrdinalIgnoreCase));

        var cachePart = existingPart?.PivotTableCacheDefinitionPart ?? wbPart.AddNewPart<PivotTableCacheDefinitionPart>();
        var recordsPart = cachePart.PivotTableCacheRecordsPart ?? cachePart.AddNewPart<PivotTableCacheRecordsPart>();

        var sourceSheet = FindSheetForWrite(doc, wbPart, pivot, target);
        var fields = BuildFieldNames(pivot, sourceSheet);
        var rowIndex = IndexOfField(fields, pivot.RowField);
        var columnIndex = IndexOfField(fields, pivot.ColumnField);
        var valueIndex = IndexOfField(fields, pivot.ValueField);
        var rows = ReadDataRows(pivot, sourceSheet);

        WriteCache(cachePart, recordsPart, pivot, fields, rows, target);
        WriteDefinition(wbPart, target, existingPart, cachePart, pivot, name, cacheId,
            fields, rowIndex, columnIndex, valueIndex);
    }

    private static ExcelSheet? FindSheetForWrite(OfficeDocument doc, WorkbookPart wbPart, PivotTableModel pivot, WorksheetPart fallback)
    {
        var sheet = FindSheet(doc, pivot.SourceSheet) ?? SheetFromPackage(wbPart, pivot.SourceSheet);
        if (sheet is not null) return sheet;
        var element = wbPart.Workbook.Sheets?.Elements<S.Sheet>()
            .FirstOrDefault(s => s.Id?.Value is { } id && wbPart.GetPartById(id) as WorksheetPart == fallback);
        return element is null ? null : SheetFromPackage(wbPart, element.Name?.Value ?? string.Empty);
    }

    private static List<string> BuildFieldNames(PivotTableModel pivot, ExcelSheet? sheet)
    {
        var fields = pivot.Fields.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
        if (fields.Count > 0) return fields;
        if (sheet is not null &&
            TryParseRange(pivot.SourceRange, out var row, out var first, out _, out var last))
            return Enumerable.Range(first, last - first + 1)
                .Select(c => sheet.Get(row, c)?.Display ?? string.Empty).ToList();
        return new List<string>();
    }

    private static int IndexOfField(IReadOnlyList<string> fields, string field)
    {
        if (string.IsNullOrWhiteSpace(field)) return -1;
        for (var i = 0; i < fields.Count; i++)
            if (string.Equals(fields[i], field, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static List<List<string>> ReadDataRows(PivotTableModel pivot, ExcelSheet? sheet)
    {
        var result = new List<List<string>>();
        if (sheet is null || !TryParseRange(pivot.SourceRange, out var firstRow, out var firstCol, out var lastRow, out var lastCol))
            return result;

        var header = Enumerable.Range(firstCol, lastCol - firstCol + 1)
            .Select(c => sheet.Get(firstRow, c)?.Display ?? string.Empty).ToList();
        var wanted = pivot.Fields.Count > 0
            ? pivot.Fields.Select(f => IndexOfField(header, f)).ToList()
            : Enumerable.Range(0, header.Count).ToList();

        for (var row = firstRow + 1; row <= lastRow; row++)
        {
            var values = wanted.Select(index =>
                index >= 0 && index < header.Count ? sheet.Get(row, firstCol + index)?.Display ?? string.Empty : string.Empty).ToList();
            result.Add(values);
        }
        return result;
    }

    private static void WriteCache(PivotTableCacheDefinitionPart cachePart, PivotTableCacheRecordsPart recordsPart,
        PivotTableModel pivot, IReadOnlyList<string> fields, IReadOnlyList<List<string>> rows, WorksheetPart target)
    {
        var cacheFields = new S.CacheFields { Count = (uint)fields.Count };
        var itemIndices = new List<Dictionary<string, int>>();

        for (var i = 0; i < fields.Count; i++)
        {
            var shared = new S.SharedItems();
            var indices = new Dictionary<string, int>(StringComparer.Ordinal);
            var hasNumber = false;
            var hasString = false;
            foreach (var row in rows)
            {
                var value = row.ElementAtOrDefault(i) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(value) || indices.ContainsKey(value)) continue;
                var index = indices.Count;
                indices.Add(value, index);
                if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
                {
                    shared.AppendChild(new S.NumberItem { Val = number });
                    hasNumber = true;
                }
                else
                {
                    shared.AppendChild(new S.StringItem { Val = value });
                    hasString = true;
                }
            }
            shared.Count = (uint)indices.Count;
            shared.ContainsNumber = hasNumber;
            shared.ContainsString = hasString;
            shared.ContainsSemiMixedTypes = hasNumber && hasString;
            itemIndices.Add(indices);
            cacheFields.AppendChild(new S.CacheField { Name = fields[i], SharedItems = shared });
        }

        var records = new S.PivotCacheRecords();
        foreach (var row in rows)
        {
            var record = new S.PivotCacheRecord();
            for (var i = 0; i < fields.Count; i++)
            {
                var value = row.ElementAtOrDefault(i) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (itemIndices[i].TryGetValue(value, out var item))
                    record.AppendChild(new S.FieldItem { Val = (uint)item });
                else
                    record.AppendChild(new S.StringItem { Val = value });
            }
            records.AppendChild(record);
        }
        records.Count = (uint)rows.Count;

        S.WorksheetSource? source = null;
        try
        {
            // The worksheet source may carry an r:id back to the source worksheet.
            cachePart.AddPart(target);
            source = new S.WorksheetSource
            {
                Sheet = pivot.SourceSheet,
                Reference = pivot.SourceRange,
                Id = cachePart.GetIdOfPart(target)
            };
        }
        catch
        {
            source = new S.WorksheetSource { Sheet = pivot.SourceSheet, Reference = pivot.SourceRange };
        }

        var cache = new S.PivotCacheDefinition
        {
            CacheSource = new S.CacheSource { Type = S.SourceValues.Worksheet, WorksheetSource = source },
            CacheFields = cacheFields,
            RecordCount = (uint)rows.Count,
            SaveData = true,
            CreatedVersion = 3,
            RefreshedVersion = 3,
            MinRefreshableVersion = 3
        };
        cachePart.PivotCacheDefinition = cache;
        cache.Save(cachePart);
        records.Save(recordsPart);
    }

    private static void WriteDefinition(WorkbookPart wbPart, WorksheetPart target, PivotTablePart? existingPart,
        PivotTableCacheDefinitionPart cachePart, PivotTableModel pivot, string name, uint cacheId,
        IReadOnlyList<string> fields, int rowIndex, int columnIndex, int valueIndex)
    {
        var dataFields = new S.DataFields { Count = 1 };
        dataFields.AppendChild(new S.DataField
        {
            Name = string.IsNullOrWhiteSpace(pivot.ValueField) ? "Value" : $"{pivot.Aggregation} of {pivot.ValueField}",
            Field = (uint)Math.Max(0, valueIndex),
            Subtotal = FromAggregation(pivot.Aggregation)
        });

        var pivotFields = new S.PivotFields { Count = (uint)fields.Count };
        for (var i = 0; i < fields.Count; i++)
        {
            var field = new S.PivotField();
            if (i == rowIndex) field.Axis = S.PivotTableAxisValues.AxisRow;
            else if (i == columnIndex) field.Axis = S.PivotTableAxisValues.AxisColumn;
            else if (i == valueIndex) field.DataField = true;
            pivotFields.AppendChild(field);
        }

        var location = new S.Location
        {
            Reference = LocationReference(existingPart?.PivotTableDefinition?.Location?.Reference?.Value, pivot.Grid),
            FirstHeaderRow = 1U,
            FirstDataRow = 1U,
            FirstDataColumn = 1U
        };

        var definition = new S.PivotTableDefinition
        {
            Name = name,
            CacheId = cacheId,
            Location = location,
            PivotFields = pivotFields,
            RowFields = FieldIndexContainer<S.RowFields>(rowIndex),
            ColumnFields = FieldIndexContainer<S.ColumnFields>(columnIndex),
            DataFields = dataFields,
            RowGrandTotals = true,
            ColumnGrandTotals = true,
            PivotTableStyle = new S.PivotTableStyle
            {
                Name = "PivotStyleLight16",
                ShowRowHeaders = true,
                ShowColumnHeaders = true,
                ShowRowStripes = true
            }
        };

        PivotTablePart pivotPart;
        if (existingPart is not null)
        {
            pivotPart = existingPart;
        }
        else
        {
            pivotPart = target.AddNewPart<PivotTablePart>();
            pivotPart.AddPart(cachePart);
        }

        pivotPart.PivotTableDefinition = definition;
        definition.Save(pivotPart);

        var relationshipId = target.GetIdOfPart(pivotPart);
        AddWorksheetPivotReference(target, relationshipId);
        AddWorkbookCache(wbPart, cachePart, cacheId);
    }

    private static T FieldIndexContainer<T>(int index) where T : OpenXmlCompositeElement, new()
    {
        var container = new T();
        container.SetAttribute(new OpenXmlAttribute("count", string.Empty, index >= 0 ? "1" : "0"));
        if (index >= 0)
        {
            var fieldIndex = new OpenXmlUnknownElement("fieldIndex", MainNs);
            fieldIndex.SetAttribute(new OpenXmlAttribute("x", string.Empty, index.ToString(CultureInfo.InvariantCulture)));
            container.AppendChild(fieldIndex);
        }
        return container;
    }

    private static void AddWorksheetPivotReference(WorksheetPart target, string relationshipId)
    {
        var worksheet = target.Worksheet;
        var container = worksheet.ChildElements
            .OfType<OpenXmlUnknownElement>()
            .FirstOrDefault(e => string.Equals(e.LocalName, "pivotTableParts", StringComparison.Ordinal));
        if (container is null)
        {
            container = new OpenXmlUnknownElement("pivotTableParts", MainNs);
            worksheet.AppendChild(container);
        }
        foreach (var child in container.ChildElements)
        {
            if (!string.Equals(child.LocalName, "pivotTablePart", StringComparison.Ordinal)) continue;
            if (string.Equals(child.GetAttribute("id", RelNs).Value, relationshipId, StringComparison.Ordinal)) return;
        }
        var reference = new OpenXmlUnknownElement("pivotTablePart", MainNs);
        reference.SetAttribute(new OpenXmlAttribute("id", RelNs, relationshipId));
        container.AppendChild(reference);
        container.SetAttribute(new OpenXmlAttribute("count", string.Empty,
            container.ChildElements.Count.ToString(CultureInfo.InvariantCulture)));
    }

    private static void AddWorkbookCache(WorkbookPart wbPart, PivotTableCacheDefinitionPart cachePart, uint cacheId)
    {
        var caches = wbPart.Workbook.GetFirstChild<S.PivotCaches>() ?? wbPart.Workbook.AppendChild(new S.PivotCaches());
        var relationshipId = wbPart.GetIdOfPart(cachePart);
        if (caches.Elements<S.PivotCache>().Any(c => c.Id?.Value == relationshipId)) return;
        caches.AppendChild(new S.PivotCache { CacheId = cacheId, Id = relationshipId });
    }

    private static string LocationReference(string? existing, List<List<string>> grid)
    {
        var start = existing?.Split(':')[0];
        if (string.IsNullOrWhiteSpace(start)) start = "A3";
        var (row, column) = ExcelSheet.ParseReference(start);
        if (row < 1 || column < 1) { row = 3; column = 1; start = ExcelSheet.CellReference(row, column); }

        var rowCount = Math.Max(2, grid.Count);
        var columnCount = grid.Count == 0 ? 2 : grid.Max(r => r.Count);
        columnCount = Math.Max(2, columnCount);
        return $"{start}:{ExcelSheet.CellReference(row + rowCount - 1, column + columnCount - 1)}";
    }

    // ------------------------------------------------------------------ aggregation

    private sealed class Accumulator
    {
        private int _numericCount;
        private int _nonEmptyCount;
        private double _sum;
        private double _min = double.MaxValue;
        private double _max = double.MinValue;

        public void Add(string? raw, PivotAggregation aggregation)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            _nonEmptyCount++;
            if (aggregation != PivotAggregation.Count &&
                double.TryParse(raw.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            {
                _numericCount++;
                _sum += value;
                _min = Math.Min(_min, value);
                _max = Math.Max(_max, value);
            }
        }

        public string Format(PivotAggregation aggregation) => aggregation switch
        {
            PivotAggregation.Count => _nonEmptyCount.ToString(CultureInfo.InvariantCulture),
            PivotAggregation.Average => _numericCount == 0 ? string.Empty : FormatNumber(_sum / _numericCount),
            PivotAggregation.Min => _numericCount == 0 ? string.Empty : FormatNumber(_min),
            PivotAggregation.Max => _numericCount == 0 ? string.Empty : FormatNumber(_max),
            _ => FormatNumber(_sum)
        };
    }

    private static string FormatNumber(double value) => value.ToString("0.##########", CultureInfo.InvariantCulture);

    private static void RecalculateCore(PivotTableModel pivot, ExcelSheet sheet)
    {
        if (!TryParseRange(pivot.SourceRange, out var firstRow, out var firstColumn, out var lastRow, out var lastColumn))
            return;

        var headers = Enumerable.Range(firstColumn, lastColumn - firstColumn + 1)
            .Select(c => sheet.Get(firstRow, c)?.Display ?? string.Empty).ToList();
        if (pivot.Fields.Count == 0)
            foreach (var header in headers) pivot.Fields.Add(header);
        var rowIndex = IndexOfField(headers, pivot.RowField);
        var columnIndex = IndexOfField(headers, pivot.ColumnField);
        var valueIndex = IndexOfField(headers, pivot.ValueField);
        if (!string.IsNullOrWhiteSpace(pivot.ValueField) && valueIndex < 0) return;

        var buckets = new Dictionary<(string Row, string Column), Accumulator>();
        var rowTotals = new Dictionary<string, Accumulator>(StringComparer.Ordinal);
        var columnTotals = new Dictionary<string, Accumulator>(StringComparer.Ordinal);
        var grandTotal = new Accumulator();
        var rowKeys = new SortedSet<string>(StringComparer.Ordinal);
        var columnKeys = new SortedSet<string>(StringComparer.Ordinal);

        for (var row = firstRow + 1; row <= lastRow; row++)
        {
            var rowKey = rowIndex < 0 ? string.Empty : sheet.Get(row, firstColumn + rowIndex)?.Display ?? string.Empty;
            var columnKey = columnIndex < 0 ? string.Empty : sheet.Get(row, firstColumn + columnIndex)?.Display ?? string.Empty;
            var raw = valueIndex < 0 ? string.Empty : sheet.Get(row, firstColumn + valueIndex)?.Display ?? string.Empty;

            rowKeys.Add(rowKey);
            columnKeys.Add(columnKey);
            var key = (rowKey, columnKey);
            if (!buckets.TryGetValue(key, out var bucket)) buckets[key] = bucket = new Accumulator();
            if (!rowTotals.TryGetValue(rowKey, out var rowAcc)) rowTotals[rowKey] = rowAcc = new Accumulator();
            if (!columnTotals.TryGetValue(columnKey, out var columnAcc)) columnTotals[columnKey] = columnAcc = new Accumulator();

            bucket.Add(raw, pivot.Aggregation);
            rowAcc.Add(raw, pivot.Aggregation);
            columnAcc.Add(raw, pivot.Aggregation);
            grandTotal.Add(raw, pivot.Aggregation);
        }

        pivot.Grid.Clear();
        var headerRow = new List<string> { pivot.RowField };
        foreach (var key in columnKeys) headerRow.Add(key);
        headerRow.Add(GrandTotal);
        pivot.Grid.Add(headerRow);

        foreach (var rowKey in rowKeys)
        {
            var line = new List<string> { rowKey };
            foreach (var columnKey in columnKeys)
                line.Add(buckets.TryGetValue((rowKey, columnKey), out var bucket)
                    ? bucket.Format(pivot.Aggregation)
                    : new Accumulator().Format(pivot.Aggregation));
            line.Add(rowTotals[rowKey].Format(pivot.Aggregation));
            pivot.Grid.Add(line);
        }

        var totalLine = new List<string> { GrandTotal };
        foreach (var columnKey in columnKeys) totalLine.Add(columnTotals[columnKey].Format(pivot.Aggregation));
        totalLine.Add(grandTotal.Format(pivot.Aggregation));
        pivot.Grid.Add(totalLine);
    }

    // ------------------------------------------------------------------ helpers

    private static ExcelSheet? FindSheet(OfficeDocument doc, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return doc.Sheets.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static ExcelSheet? SheetFromPackage(WorkbookPart wbPart, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var sheet = wbPart.Workbook.Sheets?.Elements<S.Sheet>()
            .FirstOrDefault(s => string.Equals(s.Name?.Value, name, StringComparison.OrdinalIgnoreCase));
        if (sheet?.Id?.Value is not { } id || wbPart.GetPartById(id) is not WorksheetPart part) return null;
        var model = new ExcelSheet { Name = name };
        var shared = wbPart.SharedStringTablePart?.SharedStringTable;
        var cells = part.Worksheet.GetFirstChild<S.SheetData>()?.Elements<S.Row>()
            .SelectMany(r => r.Elements<S.Cell>()) ?? Enumerable.Empty<S.Cell>();
        foreach (var cell in cells)
        {
            var (row, column) = ExcelSheet.ParseReference(cell.CellReference?.Value);
            if (row < 1 || column < 1) continue;
            model.Ensure(row, column).Display = CellText(cell, shared);
        }
        return model;
    }

    private static string CellText(S.Cell cell, S.SharedStringTable? shared)
    {
        var raw = cell.CellValue?.InnerText ?? string.Empty;
        if (cell.DataType?.Value == S.CellValues.SharedString &&
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && shared is not null)
            return shared.Elements<S.SharedStringItem>().ElementAtOrDefault(index)?.InnerText ?? string.Empty;
        if (cell.DataType?.Value == S.CellValues.InlineString)
            return cell.Elements<S.InlineString>().FirstOrDefault()?.InnerText ?? string.Empty;
        return raw;
    }

    private static void SplitSource(string? value, out string sheet, out string range)
    {
        sheet = string.Empty;
        range = value ?? string.Empty;
        var bang = range.LastIndexOf('!');
        if (bang < 0) return;
        sheet = range[..bang].Trim('\'', '"');
        range = range[(bang + 1)..];
    }

    private static bool TryParseRange(string? value, out int firstRow, out int firstColumn,
        out int lastRow, out int lastColumn)
    {
        firstRow = firstColumn = lastRow = lastColumn = 0;
        SplitSource(value, out _, out var range);
        range = range.Replace("$", string.Empty).Trim();
        var colon = range.IndexOf(':');
        if (colon < 0) return false;
        var first = ExcelSheet.ParseReference(range[..colon].Trim());
        var last = ExcelSheet.ParseReference(range[(colon + 1)..].Trim());
        if (first.Row < 1 || first.Column < 1 || last.Row < 1 || last.Column < 1) return false;
        firstRow = Math.Min(first.Row, last.Row);
        lastRow = Math.Max(first.Row, last.Row);
        firstColumn = Math.Min(first.Column, last.Column);
        lastColumn = Math.Max(first.Column, last.Column);
        return true;
    }
}
