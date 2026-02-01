using ClosedXML.Excel;
using DataverseUnmanagedLayerFixer.Models;
using System.Windows.Forms;

namespace DataverseUnmanagedLayerFixer.Services;

/// <summary>
/// Service for exporting component results to Excel.
/// </summary>
public class ExcelExportService
{
    /// <summary>
    /// Shows a Windows save file dialog and returns the selected path.
    /// </summary>
    public string? ShowSaveFileDialog(string defaultFileName)
    {
        Console.WriteLine("Opening file save dialog...");

        string? selectedPath = null;

        var thread = new Thread(() =>
        {
            using var saveDialog = new SaveFileDialog
            {
                Title = "Save Unmanaged Layers Report",
                Filter = "Excel Files (*.xlsx)|*.xlsx|All Files (*.*)|*.*",
                DefaultExt = "xlsx",
                FileName = defaultFileName,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                OverwritePrompt = true
            };

            if (saveDialog.ShowDialog() == DialogResult.OK)
            {
                selectedPath = saveDialog.FileName;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return selectedPath;
    }

    /// <summary>
    /// Exports all results to an Excel file.
    /// </summary>
    public void ExportToExcel(Dictionary<string, List<ComponentResult>> allResults, string filePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var workbook = new XLWorkbook();

            CreateSummarySheet(workbook, allResults);
            CreateDetailsSheet(workbook, allResults);

            workbook.SaveAs(filePath);

            Console.WriteLine();
            Console.WriteLine($"Results exported to: {Path.GetFullPath(filePath)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error exporting to Excel: {ex.Message}");
        }
    }

    private void CreateSummarySheet(XLWorkbook workbook, Dictionary<string, List<ComponentResult>> allResults)
    {
        var summarySheet = workbook.Worksheets.Add("Summary");

        // Title
        summarySheet.Cell(1, 1).Value = "Unmanaged Customizations Report - Summary";
        summarySheet.Cell(1, 1).Style.Font.Bold = true;
        summarySheet.Cell(1, 1).Style.Font.FontSize = 16;
        summarySheet.Range(1, 1, 1, 5).Merge();

        summarySheet.Cell(2, 1).Value = $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        summarySheet.Range(2, 1, 2, 5).Merge();

        // Calculate totals
        var allComponents = allResults.Values.SelectMany(r => r).ToList();
        int totalComponents = allComponents.Count;
        int totalRemoved = allComponents.Count(r => r.WasRemoved);
        int totalSkipped = allComponents.Count(r => !r.WasRemoved && r.RemovalStatus != "Removal Failed");
        int totalFailed = allComponents.Count(r => r.RemovalStatus == "Removal Failed");

        // Overall Statistics
        summarySheet.Cell(4, 1).Value = "Overall Statistics";
        summarySheet.Cell(4, 1).Style.Font.Bold = true;
        summarySheet.Cell(4, 1).Style.Font.FontSize = 12;

        summarySheet.Cell(5, 1).Value = "Total Solutions Processed:";
        summarySheet.Cell(5, 2).Value = allResults.Count;
        summarySheet.Cell(6, 1).Value = "Total Unmanaged Customizations:";
        summarySheet.Cell(6, 2).Value = totalComponents;
        summarySheet.Cell(7, 1).Value = "Total Removed:";
        summarySheet.Cell(7, 2).Value = totalRemoved;
        summarySheet.Cell(7, 2).Style.Fill.BackgroundColor = XLColor.LightGreen;
        summarySheet.Cell(8, 1).Value = "Total Skipped:";
        summarySheet.Cell(8, 2).Value = totalSkipped;
        summarySheet.Cell(8, 2).Style.Fill.BackgroundColor = XLColor.LightYellow;
        summarySheet.Cell(9, 1).Value = "Total Failed:";
        summarySheet.Cell(9, 2).Value = totalFailed;
        summarySheet.Cell(9, 2).Style.Fill.BackgroundColor = XLColor.LightCoral;

        // Breakdown by Solution
        AddSolutionBreakdown(summarySheet, allResults);

        // Breakdown by Component Type
        int typeStartRow = 13 + allResults.Count + 2;
        AddComponentTypeBreakdown(summarySheet, allComponents, typeStartRow);

        summarySheet.Columns().AdjustToContents();
    }

    private void AddSolutionBreakdown(IXLWorksheet sheet, Dictionary<string, List<ComponentResult>> allResults)
    {
        sheet.Cell(11, 1).Value = "Breakdown by Solution";
        sheet.Cell(11, 1).Style.Font.Bold = true;
        sheet.Cell(11, 1).Style.Font.FontSize = 12;

        var headers = new[] { "Solution", "Total", "Removed", "Skipped", "Failed" };
        for (int i = 0; i < headers.Length; i++)
        {
            sheet.Cell(12, i + 1).Value = headers[i];
            sheet.Cell(12, i + 1).Style.Font.Bold = true;
            sheet.Cell(12, i + 1).Style.Fill.BackgroundColor = XLColor.LightGray;
            sheet.Cell(12, i + 1).Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        }

        int row = 13;
        foreach (var (solutionName, results) in allResults)
        {
            int removed = results.Count(r => r.WasRemoved);
            int skipped = results.Count(r => !r.WasRemoved && r.RemovalStatus != "Removal Failed");
            int failed = results.Count(r => r.RemovalStatus == "Removal Failed");

            sheet.Cell(row, 1).Value = solutionName;
            sheet.Cell(row, 2).Value = results.Count;
            sheet.Cell(row, 3).Value = removed;
            sheet.Cell(row, 4).Value = skipped;
            sheet.Cell(row, 5).Value = failed;
            row++;
        }
    }

    private void AddComponentTypeBreakdown(IXLWorksheet sheet, List<ComponentResult> allComponents, int startRow)
    {
        sheet.Cell(startRow, 1).Value = "Breakdown by Component Type";
        sheet.Cell(startRow, 1).Style.Font.Bold = true;
        sheet.Cell(startRow, 1).Style.Font.FontSize = 12;

        var headers = new[] { "Component Type", "Total", "Removed", "Skipped", "Failed" };
        for (int i = 0; i < headers.Length; i++)
        {
            sheet.Cell(startRow + 1, i + 1).Value = headers[i];
            sheet.Cell(startRow + 1, i + 1).Style.Font.Bold = true;
            sheet.Cell(startRow + 1, i + 1).Style.Fill.BackgroundColor = XLColor.LightGray;
            sheet.Cell(startRow + 1, i + 1).Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        }

        var groups = allComponents
            .GroupBy(c => c.ComponentType)
            .OrderByDescending(g => g.Count());

        int row = startRow + 2;
        foreach (var group in groups)
        {
            int removed = group.Count(r => r.WasRemoved);
            int skipped = group.Count(r => !r.WasRemoved && r.RemovalStatus != "Removal Failed");
            int failed = group.Count(r => r.RemovalStatus == "Removal Failed");

            sheet.Cell(row, 1).Value = group.Key;
            sheet.Cell(row, 2).Value = group.Count();
            sheet.Cell(row, 3).Value = removed;
            sheet.Cell(row, 4).Value = skipped;
            sheet.Cell(row, 5).Value = failed;
            row++;
        }
    }

    private void CreateDetailsSheet(XLWorkbook workbook, Dictionary<string, List<ComponentResult>> allResults)
    {
        var detailsSheet = workbook.Worksheets.Add("Details");

        // Title
        detailsSheet.Cell(1, 1).Value = "Unmanaged Customizations Report - All Components";
        detailsSheet.Cell(1, 1).Style.Font.Bold = true;
        detailsSheet.Cell(1, 1).Style.Font.FontSize = 14;
        detailsSheet.Range(1, 1, 1, 9).Merge();

        detailsSheet.Cell(2, 1).Value = $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        detailsSheet.Range(2, 1, 2, 9).Merge();

        // Headers
        var headers = new[] { "Solution", "Component Name", "Component Type", "Component ID", "Entity", "Solution Layer", "Modified On", "Modified By", "Removal Status" };
        int headerRow = 4;

        for (int i = 0; i < headers.Length; i++)
        {
            detailsSheet.Cell(headerRow, i + 1).Value = headers[i];
            detailsSheet.Cell(headerRow, i + 1).Style.Font.Bold = true;
            detailsSheet.Cell(headerRow, i + 1).Style.Fill.BackgroundColor = XLColor.LightGray;
            detailsSheet.Cell(headerRow, i + 1).Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        }

        // Data
        int dataRow = headerRow + 1;
        foreach (var (solutionName, results) in allResults)
        {
            foreach (var result in results)
            {
                detailsSheet.Cell(dataRow, 1).Value = solutionName;
                detailsSheet.Cell(dataRow, 2).Value = result.ComponentName;
                detailsSheet.Cell(dataRow, 3).Value = result.ComponentType;
                detailsSheet.Cell(dataRow, 4).Value = result.ComponentId.ToString();
                detailsSheet.Cell(dataRow, 5).Value = result.EntityName;
                detailsSheet.Cell(dataRow, 6).Value = result.SolutionLayer;
                detailsSheet.Cell(dataRow, 7).Value = result.ModifiedOn?.ToString("yyyy-MM-dd HH:mm:ss") ?? "N/A";
                detailsSheet.Cell(dataRow, 8).Value = result.ModifiedBy;
                detailsSheet.Cell(dataRow, 9).Value = result.RemovalStatus;

                // Color-code status
                var statusCell = detailsSheet.Cell(dataRow, 9);
                statusCell.Style.Fill.BackgroundColor = result.RemovalStatus switch
                {
                    "Removed" => XLColor.LightGreen,
                    "Removal Failed" => XLColor.LightCoral,
                    _ => XLColor.LightYellow
                };

                dataRow++;
            }
        }

        detailsSheet.Columns().AdjustToContents();
    }
}
