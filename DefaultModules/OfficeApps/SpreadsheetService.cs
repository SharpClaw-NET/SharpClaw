using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;

namespace SharpClaw.Modules.OfficeApps;

/// <summary>
/// File-based spreadsheet operations using ClosedXML (.xlsx/.xlsm) and
/// CsvHelper (.csv).  All methods operate directly on the file system.
/// If the file is locked (e.g. open in Excel), writes will throw
/// <see cref="IOException"/> — the agent should then use the live COM
/// tools instead.
/// </summary>
public sealed class SpreadsheetService
{
    /// <summary>
    /// Read a rectangular range and return as a JSON grid.
    /// e.g. <c>[["Name","Age"],["Alice",30],["Bob",25]]</c>
    /// </summary>
    public string ReadRange(string filePath, string? sheetName, string? range)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        return IsCsv(filePath)
            ? ReadRangeCsv(filePath, range)
            : ReadRangeXlsx(filePath, sheetName, range);
    }

    /// <summary>
    /// Write a JSON grid (or single value) to a range.
    /// Supports formulas (strings starting with '=').
    /// </summary>
    public string WriteRange(string filePath, string? sheetName, string? range, JsonElement data)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        return IsCsv(filePath)
            ? WriteRangeCsv(filePath, range, data)
            : WriteRangeXlsx(filePath, sheetName, range, data);
    }

    /// <summary>
    /// List all sheet names + basic metadata (row/col counts).
    /// CSV files return a single-entry list.
    /// </summary>
    public string ListSheets(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        if (IsCsv(filePath))
        {
            var (rows, cols) = CountCsvDimensions(filePath);
            return JsonSerializer.Serialize(new[]
            {
                new { name = "Sheet1", rows, cols }
            });
        }

        using var workbook = new XLWorkbook(filePath);
        var sheets = workbook.Worksheets.Select(ws =>
        {
            var used = ws.RangeUsed();
            return new
            {
                name = ws.Name,
                rows = used?.RowCount() ?? 0,
                cols = used?.ColumnCount() ?? 0,
            };
        });
        return JsonSerializer.Serialize(sheets);
    }

    /// <summary>
    /// Create a new sheet in an existing workbook.
    /// CSV files throw (single-sheet only).
    /// </summary>
    public string CreateSheet(string filePath, string sheetName)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sheetName);

        if (IsCsv(filePath))
            throw new InvalidOperationException("CSV files do not support multiple sheets.");

        using var workbook = new XLWorkbook(filePath);
        workbook.Worksheets.Add(sheetName);
        workbook.Save();
        return $"Created sheet '{sheetName}' in {Path.GetFileName(filePath)}.";
    }

    /// <summary>
    /// Delete a sheet from an existing workbook.
    /// CSV files throw (single-sheet only).
    /// </summary>
    public string DeleteSheet(string filePath, string sheetName)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sheetName);

        if (IsCsv(filePath))
            throw new InvalidOperationException("CSV files do not support multiple sheets.");

        using var workbook = new XLWorkbook(filePath);
        var ws = workbook.Worksheet(sheetName)
            ?? throw new InvalidOperationException($"Sheet '{sheetName}' not found.");
        ws.Delete();
        workbook.Save();
        return $"Deleted sheet '{sheetName}' from {Path.GetFileName(filePath)}.";
    }

    /// <summary>
    /// Workbook-level info: sheets, named ranges, file size, last modified.
    /// </summary>
    public string GetInfo(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
            throw new FileNotFoundException($"File not found: {filePath}");

        if (IsCsv(filePath))
        {
            var (rows, cols) = CountCsvDimensions(filePath);
            return JsonSerializer.Serialize(new
            {
                fileName = fileInfo.Name,
                fileSizeBytes = fileInfo.Length,
                lastModified = fileInfo.LastWriteTimeUtc,
                documentType = "Csv",
                sheets = new[] { new { name = "Sheet1", rows, cols } },
            });
        }

        using var workbook = new XLWorkbook(filePath);
        var sheets = workbook.Worksheets.Select(ws =>
        {
            var used = ws.RangeUsed();
            return new
            {
                name = ws.Name,
                rows = used?.RowCount() ?? 0,
                cols = used?.ColumnCount() ?? 0,
            };
        }).ToList();

        var namedRanges = workbook.DefinedNames
            .Select(nr => new { name = nr.Name, refersTo = nr.RefersTo })
            .ToList();

        return JsonSerializer.Serialize(new
        {
            fileName = fileInfo.Name,
            fileSizeBytes = fileInfo.Length,
            lastModified = fileInfo.LastWriteTimeUtc,
            documentType = "Spreadsheet",
            sheets,
            namedRanges,
        });
    }

    /// <summary>
    /// Create a brand new .xlsx or .csv with optional initial data.
    /// </summary>
    public string CreateWorkbook(string filePath, string? initialSheetName, JsonElement? initialData)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        var fullPath = Path.GetFullPath(filePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        if (IsCsv(fullPath))
        {
            using var writer = new StreamWriter(fullPath);
            if (initialData.HasValue && initialData.Value.ValueKind == JsonValueKind.Array)
            {
                using var csvWriter = new CsvWriter(writer, CultureInfo.InvariantCulture);
                WriteGridToCsv(csvWriter, initialData.Value);
            }
            return $"Created CSV file at {fullPath}.";
        }

        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add(initialSheetName ?? "Sheet1");

        if (initialData.HasValue && initialData.Value.ValueKind == JsonValueKind.Array)
        {
            WriteGridToWorksheet(ws, 1, 1, initialData.Value);
        }

        workbook.SaveAs(fullPath);
        return $"Created workbook at {fullPath}.";
    }

    // ═══════════════════════════════════════════════════════════════
    // XLSX / XLSM — ClosedXML
    // ═══════════════════════════════════════════════════════════════

    private static string ReadRangeXlsx(string filePath, string? sheetName, string? range)
    {
        using var workbook = new XLWorkbook(filePath);
        var ws = sheetName is not null
            ? workbook.Worksheet(sheetName)
                ?? throw new InvalidOperationException($"Sheet '{sheetName}' not found.")
            : workbook.Worksheet(1);

        IXLRange targetRange;
        if (string.IsNullOrEmpty(range))
        {
            var used = ws.RangeUsed();
            if (used is null)
                return "[]";
            targetRange = used;
        }
        else
        {
            targetRange = ws.Range(range);
        }

        var result = new List<List<object?>>();
        foreach (var row in targetRange.Rows())
        {
            var rowData = new List<object?>();
            foreach (var cell in row.Cells())
            {
                rowData.Add(GetCellValue(cell));
            }
            result.Add(rowData);
        }

        return JsonSerializer.Serialize(result);
    }

    private static string WriteRangeXlsx(string filePath, string? sheetName, string? range, JsonElement data)
    {
        using var workbook = new XLWorkbook(filePath);
        var ws = sheetName is not null
            ? workbook.Worksheet(sheetName)
                ?? throw new InvalidOperationException($"Sheet '{sheetName}' not found.")
            : workbook.Worksheet(1);

        var (startRow, startCol) = ParseRangeStart(range);
        int cellCount = WriteGridToWorksheet(ws, startRow, startCol, data);

        workbook.Save();
        var rangeDesc = range ?? $"A1";
        return $"Wrote {cellCount} cells to {ws.Name}!{rangeDesc}.";
    }

    // ═══════════════════════════════════════════════════════════════
    // CSV — CsvHelper
    // ═══════════════════════════════════════════════════════════════

    private static string ReadRangeCsv(string filePath, string? range)
    {
        var allRows = ReadAllCsvRows(filePath);

        if (string.IsNullOrEmpty(range))
            return JsonSerializer.Serialize(allRows);

        var (startRow, startCol, endRow, endCol) = ParseFullRange(range, allRows.Count,
            allRows.Count > 0 ? allRows[0].Count : 0);

        var result = new List<List<object?>>();
        for (int r = startRow; r <= endRow && r < allRows.Count; r++)
        {
            var rowData = new List<object?>();
            for (int c = startCol; c <= endCol && c < allRows[r].Count; c++)
            {
                rowData.Add(allRows[r][c]);
            }
            result.Add(rowData);
        }

        return JsonSerializer.Serialize(result);
    }

    private static string WriteRangeCsv(string filePath, string? range, JsonElement data)
    {
        var allRows = File.Exists(filePath) ? ReadAllCsvRows(filePath) : [];
        var (startRow, startCol) = ParseRangeStart(range);

        // Zero-based for list indexing
        int r0 = startRow - 1;
        int c0 = startCol - 1;

        int cellCount = 0;
        if (data.ValueKind == JsonValueKind.Array)
        {
            int rowIdx = r0;
            foreach (var rowEl in data.EnumerateArray())
            {
                while (allRows.Count <= rowIdx)
                    allRows.Add([]);

                if (rowEl.ValueKind == JsonValueKind.Array)
                {
                    int colIdx = c0;
                    foreach (var cellEl in rowEl.EnumerateArray())
                    {
                        while (allRows[rowIdx].Count <= colIdx)
                            allRows[rowIdx].Add("");
                        allRows[rowIdx][colIdx] = JsonElementToString(cellEl);
                        colIdx++;
                        cellCount++;
                    }
                }
                else
                {
                    while (allRows[rowIdx].Count <= c0)
                        allRows[rowIdx].Add("");
                    allRows[rowIdx][c0] = JsonElementToString(rowEl);
                    cellCount++;
                }
                rowIdx++;
            }
        }
        else
        {
            while (allRows.Count <= r0)
                allRows.Add([]);
            while (allRows[r0].Count <= c0)
                allRows[r0].Add("");
            allRows[r0][c0] = JsonElementToString(data);
            cellCount = 1;
        }

        // Normalize column count across all rows
        int maxCols = allRows.Max(r => r.Count);
        foreach (var row in allRows)
            while (row.Count < maxCols) row.Add("");

        // Atomic rewrite
        using var writer = new StreamWriter(filePath);
        using var csvWriter = new CsvWriter(writer, CultureInfo.InvariantCulture);
        foreach (var row in allRows)
        {
            foreach (var cell in row)
            {
                csvWriter.WriteField(cell);
            }
            csvWriter.NextRecord();
        }

        return $"Wrote {cellCount} cells to {Path.GetFileName(filePath)}.";
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private static bool IsCsv(string filePath) =>
        Path.GetExtension(filePath).Equals(".csv", StringComparison.OrdinalIgnoreCase);

    private static object? GetCellValue(IXLCell cell) => cell.DataType switch
    {
        XLDataType.Number => cell.GetDouble(),
        XLDataType.Boolean => cell.GetBoolean(),
        XLDataType.DateTime => cell.GetDateTime().ToString("O"),
        XLDataType.Blank => null,
        _ => cell.GetFormattedString(),
    };

    private static int WriteGridToWorksheet(IXLWorksheet ws, int startRow, int startCol, JsonElement data)
    {
        int cellCount = 0;
        if (data.ValueKind == JsonValueKind.Array)
        {
            int rowIdx = startRow;
            foreach (var rowEl in data.EnumerateArray())
            {
                if (rowEl.ValueKind == JsonValueKind.Array)
                {
                    int colIdx = startCol;
                    foreach (var cellEl in rowEl.EnumerateArray())
                    {
                        SetCellFromJson(ws.Cell(rowIdx, colIdx), cellEl);
                        colIdx++;
                        cellCount++;
                    }
                }
                else
                {
                    SetCellFromJson(ws.Cell(rowIdx, startCol), rowEl);
                    cellCount++;
                }
                rowIdx++;
            }
        }
        else
        {
            SetCellFromJson(ws.Cell(startRow, startCol), data);
            cellCount = 1;
        }
        return cellCount;
    }

    private static void SetCellFromJson(IXLCell cell, JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Number:
                cell.Value = el.GetDouble();
                break;
            case JsonValueKind.True:
                cell.Value = true;
                break;
            case JsonValueKind.False:
                cell.Value = false;
                break;
            case JsonValueKind.String:
                var str = el.GetString() ?? "";
                if (str.StartsWith('='))
                    cell.FormulaA1 = str;
                else
                    cell.Value = str;
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                cell.Value = Blank.Value;
                break;
            default:
                cell.Value = el.ToString();
                break;
        }
    }

    private static void WriteGridToCsv(CsvWriter csvWriter, JsonElement data)
    {
        foreach (var rowEl in data.EnumerateArray())
        {
            if (rowEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var cellEl in rowEl.EnumerateArray())
                    csvWriter.WriteField(JsonElementToString(cellEl));
            }
            else
            {
                csvWriter.WriteField(JsonElementToString(rowEl));
            }
            csvWriter.NextRecord();
        }
    }

    private static string JsonElementToString(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? "",
        JsonValueKind.Number => el.GetRawText(),
        JsonValueKind.True => "TRUE",
        JsonValueKind.False => "FALSE",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => el.ToString(),
    };

    private static List<List<string>> ReadAllCsvRows(string filePath)
    {
        var rows = new List<List<string>>();
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = false,
        };

        using var reader = new StreamReader(filePath);
        using var csvReader = new CsvReader(reader, config);

        while (csvReader.Read())
        {
            var row = new List<string>();
            for (int i = 0; csvReader.TryGetField<string>(i, out var field); i++)
                row.Add(field ?? "");
            rows.Add(row);
        }
        return rows;
    }

    private static (int rows, int cols) CountCsvDimensions(string filePath)
    {
        if (!File.Exists(filePath)) return (0, 0);
        var rows = ReadAllCsvRows(filePath);
        return (rows.Count, rows.Count > 0 ? rows.Max(r => r.Count) : 0);
    }

    /// <summary>
    /// Parses the top-left corner of a range like "A1", "B5:D10", etc.
    /// Returns 1-based row and column.
    /// </summary>
    private static (int row, int col) ParseRangeStart(string? range)
    {
        if (string.IsNullOrEmpty(range)) return (1, 1);

        // Take only the start of "A1:C10"
        var cellRef = range.Contains(':') ? range[..range.IndexOf(':')] : range;
        return ParseCellReference(cellRef);
    }

    /// <summary>
    /// Parses a full range like "A1:C10" into (startRow, startCol, endRow, endCol),
    /// all 0-based for list indexing.
    /// </summary>
    private static (int startRow, int startCol, int endRow, int endCol) ParseFullRange(
        string range, int totalRows, int totalCols)
    {
        if (range.Contains(':'))
        {
            var parts = range.Split(':');
            var (sr, sc) = ParseCellReference(parts[0]);
            var (er, ec) = ParseCellReference(parts[1]);
            return (sr - 1, sc - 1, er - 1, ec - 1);
        }

        var (r, c) = ParseCellReference(range);
        return (r - 1, c - 1, r - 1, c - 1);
    }

    private static readonly Regex CellRefPattern = new(@"^([A-Z]+)(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Parses a cell reference like "A1" into 1-based (row, col).
    /// </summary>
    private static (int row, int col) ParseCellReference(string cellRef)
    {
        var match = CellRefPattern.Match(cellRef.Trim());
        if (!match.Success)
            throw new ArgumentException($"Invalid cell reference: '{cellRef}'");

        var colStr = match.Groups[1].Value.ToUpperInvariant();
        int col = 0;
        foreach (char c in colStr)
            col = col * 26 + (c - 'A' + 1);

        int row = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return (row, col);
    }
}
