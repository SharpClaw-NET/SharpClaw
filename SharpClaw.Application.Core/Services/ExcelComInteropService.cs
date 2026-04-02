using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace SharpClaw.Application.Services;

/// <summary>
/// Windows-only COM Interop service for reading/writing data in a
/// running Excel instance. Used by the <c>spreadsheet_live_read_range</c>
/// and <c>spreadsheet_live_write_range</c> tools — agents explicitly
/// choose these when the file is open in Excel.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ExcelComInteropService
{
    /// <summary>
    /// Read a range from a workbook that is currently open in Excel.
    /// Returns a JSON grid: <c>[["A","B"],[1,2]]</c>.
    /// </summary>
    public string ReadRange(string filePath, string? sheetName, string? range)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        EnsureWindows();

        var fullPath = Path.GetFullPath(filePath);
        dynamic excel = GetExcelInstance();
        dynamic workbook = FindOpenWorkbook(excel, fullPath);
        dynamic sheet = ResolveSheet(workbook, sheetName);

        dynamic target;
        if (string.IsNullOrEmpty(range))
        {
            target = sheet.UsedRange;
        }
        else
        {
            target = sheet.Range[range];
        }

        object?[,] values = target.Value2 is object?[,] multi
            ? multi
            : new object?[,] { { target.Value2 } };

        int rows = values.GetLength(0);
        int cols = values.GetLength(1);

        var result = new List<List<object?>>();
        for (int r = 1; r <= rows; r++)
        {
            var row = new List<object?>();
            for (int c = 1; c <= cols; c++)
            {
                row.Add(NormalizeCellValue(values[r, c]));
            }
            result.Add(row);
        }

        return JsonSerializer.Serialize(result);
    }

    /// <summary>
    /// Write a JSON grid (or single value) to a range in a workbook
    /// currently open in Excel. Supports formulas (strings starting with '=').
    /// </summary>
    public string WriteRange(string filePath, string? sheetName, string? range, JsonElement data)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(range);
        EnsureWindows();

        var fullPath = Path.GetFullPath(filePath);
        dynamic excel = GetExcelInstance();
        dynamic workbook = FindOpenWorkbook(excel, fullPath);
        dynamic sheet = ResolveSheet(workbook, sheetName);

        int cellCount;

        if (data.ValueKind == JsonValueKind.Array)
        {
            var grid = JsonToGrid(data);
            int rows = grid.Count;
            int cols = grid.Max(r => r.Count);
            cellCount = rows * cols;

            // Build a 1-based 2D array for Excel
            var values = new object?[rows, cols];
            for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                values[r, c] = c < grid[r].Count ? grid[r][c] : null;

            dynamic target = sheet.Range[range];
            dynamic endCell = sheet.Cells[target.Row + rows - 1, target.Column + cols - 1];
            dynamic fullRange = sheet.Range[target, endCell];

            // Check for formulas — if any cell is a formula, write cell-by-cell
            bool hasFormulas = false;
            for (int r = 0; r < rows && !hasFormulas; r++)
            for (int c = 0; c < cols && !hasFormulas; c++)
                if (values[r, c] is string s && s.StartsWith('='))
                    hasFormulas = true;

            if (hasFormulas)
            {
                for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    dynamic cell = sheet.Cells[target.Row + r, target.Column + c];
                    var val = values[r, c];
                    if (val is string s && s.StartsWith('='))
                        cell.Formula = s;
                    else
                        cell.Value2 = val;
                }
            }
            else
            {
                fullRange.Value2 = values;
            }
        }
        else
        {
            cellCount = 1;
            dynamic target = sheet.Range[range];
            var val = JsonElementToComValue(data);
            if (val is string s && s.StartsWith('='))
                target.Formula = s;
            else
                target.Value2 = val;
        }

        return $"Wrote {cellCount} cells to {(sheetName ?? sheet.Name)}!{range} via live Excel.";
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Live Excel COM Interop is only available on Windows.");
    }

    /// <summary>
    /// Gets the running Excel.Application COM object via the Running Object Table.
    /// <c>Marshal.GetActiveObject</c> was removed in .NET 5+; this reimplements it
    /// using the native <c>GetActiveObject</c> export from oleaut32.dll.
    /// </summary>
    private static dynamic GetExcelInstance()
    {
        var hr = CLSIDFromProgID("Excel.Application", out var clsid);
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);

        hr = GetActiveObject(clsid, nint.Zero, out var obj);
        if (hr < 0 || obj is null)
            throw new InvalidOperationException(
                "Excel is not running. Use the file-based spreadsheet tools instead, " +
                "or launch Excel first with the launch_application tool.");

        return obj;
    }

    [DllImport("oleaut32.dll", PreserveSig = true)]
    private static extern int GetActiveObject(
        in Guid rclsid, nint pvReserved, [MarshalAs(UnmanagedType.IUnknown)] out object? ppunk);

    [DllImport("ole32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int CLSIDFromProgID(string lpszProgID, out Guid lpclsid);

    /// <summary>
    /// Finds an open workbook by matching the full file path.
    /// </summary>
    private static dynamic FindOpenWorkbook(dynamic excel, string fullPath)
    {
        foreach (dynamic wb in excel.Workbooks)
        {
            if (string.Equals(wb.FullName, fullPath, StringComparison.OrdinalIgnoreCase))
                return wb;
        }

        throw new InvalidOperationException(
            $"Workbook not found in running Excel: {fullPath}. " +
            "Make sure the file is open in Excel.");
    }

    private static dynamic ResolveSheet(dynamic workbook, string? sheetName)
    {
        if (sheetName is not null)
        {
            try
            {
                return workbook.Sheets[sheetName];
            }
            catch
            {
                throw new InvalidOperationException($"Sheet '{sheetName}' not found.");
            }
        }
        return workbook.ActiveSheet;
    }

    private static object? NormalizeCellValue(object? value) => value switch
    {
        null => null,
        double d when d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue => (int)d,
        double d => d,
        string s => s,
        bool b => b,
        _ => value.ToString(),
    };

    private static List<List<object?>> JsonToGrid(JsonElement data)
    {
        var grid = new List<List<object?>>();
        foreach (var rowEl in data.EnumerateArray())
        {
            var row = new List<object?>();
            if (rowEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var cellEl in rowEl.EnumerateArray())
                    row.Add(JsonElementToComValue(cellEl));
            }
            else
            {
                row.Add(JsonElementToComValue(rowEl));
            }
            grid.Add(row);
        }
        return grid;
    }

    private static object? JsonElementToComValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => el.ToString(),
    };
}
