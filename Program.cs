using DataverseUnmanagedLayerFixer.Models;
using DataverseUnmanagedLayerFixer.Services;

namespace DataverseUnmanagedLayerFixer;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("===========================================");
        Console.WriteLine("  Dataverse Unmanaged Layer Fixer Tool");
        Console.WriteLine("===========================================");
        Console.WriteLine();

        // Parse command line arguments
        var (environmentUrl, solutionName) = ParseArguments(args);

        environmentUrl ??= GetEnvironmentUrl();

        // Initialize services
        using var dataverseService = new DataverseService();

        if (!dataverseService.Connect(environmentUrl))
        {
            Console.WriteLine("Failed to connect to Dataverse. Exiting.");
            return;
        }

        Console.WriteLine();
        Console.WriteLine("Connected successfully!");
        Console.WriteLine();

        var solutionService = new SolutionService(dataverseService);
        var componentService = new ComponentService(dataverseService);
        var excelExportService = new ExcelExportService();

        // Track results across all solutions
        var allSolutionResults = new Dictionary<string, List<ComponentResult>>();

        // Main processing loop
        bool continueProcessing = true;
        while (continueProcessing)
        {
            var selectedSolutions = await solutionService.SelectSolutionsAsync(solutionName);
            if (selectedSolutions.Count == 0)
            {
                if (allSolutionResults.Count == 0)
                {
                    Console.WriteLine("No solution selected. Exiting.");
                }
                break;
            }

            // Clear solution name after first use
            solutionName = null;

            // Ask if export only mode
            Console.WriteLine();
            Console.Write("Export only (no removal prompts)? (y/n): ");
            string? exportOnlyResponse = Console.ReadLine()?.Trim().ToLower();
            bool exportOnly = exportOnlyResponse == "y" || exportOnlyResponse == "yes";

            if (exportOnly)
            {
                Console.WriteLine("Running in export-only mode - no layers will be removed.");
            }

            // Process all selected solutions
            for (int i = 0; i < selectedSolutions.Count; i++)
            {
                var solution = selectedSolutions[i];
                var solutionFriendlyName = solution.GetAttributeValue<string>("friendlyname") ?? "Unknown";

                if (selectedSolutions.Count > 1)
                {
                    Console.WriteLine();
                    Console.WriteLine($"========== Processing solution {i + 1} of {selectedSolutions.Count} ==========");
                }

                var results = await componentService.ProcessSolutionAsync(solution, exportOnly);

                if (results.Count > 0)
                {
                    allSolutionResults[solutionFriendlyName] = results;
                }
            }

            // If export only, skip to export prompt
            if (exportOnly)
            {
                break;
            }

            Console.WriteLine();
            Console.Write("Do you want to check more solutions? (y/n): ");
            string? response = Console.ReadLine()?.Trim().ToLower();
            continueProcessing = response == "y" || response == "yes";
            Console.WriteLine();
        }

        // Export results to Excel
        if (allSolutionResults.Count > 0)
        {
            PromptForExport(allSolutionResults, excelExportService);
        }

        Console.WriteLine("Processing complete. Press any key to exit.");
        Console.ReadKey();
    }

    private static (string? environmentUrl, string? solutionName) ParseArguments(string[] args)
    {
        string? environmentUrl = null;
        string? solutionName = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--solution" || args[i] == "-s")
            {
                if (i + 1 < args.Length)
                {
                    solutionName = args[i + 1];
                    i++;
                }
            }
            else if (!args[i].StartsWith("-") && environmentUrl == null)
            {
                environmentUrl = args[i];
            }
        }

        return (environmentUrl, solutionName);
    }

    private static string GetEnvironmentUrl()
    {
        Console.Write("Enter the Dataverse environment URL (e.g., https://yourorg.crm.dynamics.com): ");
        string? url = Console.ReadLine();

        while (string.IsNullOrWhiteSpace(url))
        {
            Console.Write("URL cannot be empty. Please enter the Dataverse environment URL: ");
            url = Console.ReadLine();
        }

        return url.Trim();
    }

    private static void PromptForExport(
        Dictionary<string, List<ComponentResult>> allResults,
        ExcelExportService excelExportService)
    {
        int totalComponents = allResults.Values.Sum(r => r.Count);
        Console.WriteLine($"Found {totalComponents} unmanaged customizations across {allResults.Count} solution(s).");
        Console.Write("Do you want to export results to Excel? (y/n): ");
        string? exportResponse = Console.ReadLine()?.Trim().ToLower();

        if (exportResponse == "y" || exportResponse == "yes")
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var defaultFileName = $"D365-Solution-Active-Layers-{timestamp}.xlsx";

            string? filePath = excelExportService.ShowSaveFileDialog(defaultFileName);

            if (!string.IsNullOrEmpty(filePath))
            {
                excelExportService.ExportToExcel(allResults, filePath);
            }
            else
            {
                Console.WriteLine("Export cancelled.");
            }
        }
    }
}
