using ClosedXML.Excel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using System.Net.Http.Headers;
using System.ServiceModel;
using System.Text.Json;
using System.Windows.Forms;
using System.Xml.Linq;

namespace DataverseUnmanagedLayerFixer;

// Record to track component processing results for export
record ComponentResult(
    string ComponentName,
    string ComponentType,
    Guid ComponentId,
    string EntityName,
    string SolutionLayer,
    DateTime? ModifiedOn,
    string ModifiedBy,
    bool WasRemoved,
    string RemovalStatus,
    string DiffDetails = ""
);

class Program
{
    private static ServiceClient? _serviceClient;
    private static HttpClient? _httpClient;

    static async Task Main(string[] args)
    {
        Console.WriteLine("===========================================");
        Console.WriteLine("  Dataverse Unmanaged Layer Fixer Tool");
        Console.WriteLine("===========================================");
        Console.WriteLine();

        // Parse command line arguments
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

        environmentUrl ??= GetEnvironmentUrl();

        if (!ConnectToDataverse(environmentUrl))
        {
            Console.WriteLine("Failed to connect to Dataverse. Exiting.");
            return;
        }

        Console.WriteLine();
        Console.WriteLine("Connected successfully!");
        Console.WriteLine();

        // Initialize HttpClient for Web API calls
        InitializeHttpClient();

        // Track results across all solutions for export
        var allSolutionResults = new Dictionary<string, List<ComponentResult>>();

        // Main loop - allow processing multiple solutions
        bool continueProcessing = true;
        while (continueProcessing)
        {
            var selectedSolutions = await SelectSolutionsAsync(solutionName);
            if (selectedSolutions.Count == 0)
            {
                if (allSolutionResults.Count == 0)
                {
                    Console.WriteLine("No solution selected. Exiting.");
                }
                break;
            }

            // Clear the solution name after first use (so user can select interactively next time)
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

            // Ask if user wants to see layer differences
            Console.Write("Show layer differences for forms/views? (y/n): ");
            string? showDiffsResponse = Console.ReadLine()?.Trim().ToLower();
            bool showDiffs = showDiffsResponse == "y" || showDiffsResponse == "yes";

            if (showDiffs)
            {
                Console.WriteLine("Layer differences will be calculated for forms and views.");
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

                var results = await ProcessSolutionComponentsAsync(solution, exportOnly, showDiffs);

                if (results.Count > 0)
                {
                    allSolutionResults[solutionFriendlyName] = results;
                }
            }

            // If export only, skip the "check more solutions" prompt and go straight to export
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

        // Prompt for Excel export if there are any results
        if (allSolutionResults.Count > 0)
        {
            int totalComponents = allSolutionResults.Values.Sum(r => r.Count);
            Console.WriteLine($"Found {totalComponents} unmanaged customizations across {allSolutionResults.Count} solution(s).");
            Console.Write("Do you want to export results to Excel? (y/n): ");
            string? exportResponse = Console.ReadLine()?.Trim().ToLower();

            if (exportResponse == "y" || exportResponse == "yes")
            {
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var defaultFileName = $"D365-Solution-Active-Layers-{timestamp}.xlsx";

                string? filePath = ShowSaveFileDialog(defaultFileName);

                if (!string.IsNullOrEmpty(filePath))
                {
                    ExportResultsToExcel(allSolutionResults, filePath);
                }
                else
                {
                    Console.WriteLine("Export cancelled.");
                }
            }
        }

        Console.WriteLine("Processing complete. Press any key to exit.");
        Console.ReadKey();
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

    [STAThread]
    private static string? ShowSaveFileDialog(string defaultFileName)
    {
        Console.WriteLine("Opening file save dialog...");

        string? selectedPath = null;

        // Run the dialog on an STA thread (required for Windows Forms dialogs)
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

    private static bool ConnectToDataverse(string environmentUrl)
    {
        try
        {
            Console.WriteLine($"Connecting to: {environmentUrl}");
            Console.WriteLine("A browser window will open for authentication...");

            // Build connection string for interactive authentication
            string connectionString = $@"
                AuthType=OAuth;
                Url={environmentUrl};
                LoginPrompt=Auto;
                RequireNewInstance=True;
                RedirectUri=http://localhost;
                AppId=51f81489-12ee-4a9e-aaae-a2591f45987d;
                TokenCacheStorePath=./tokencache.dat";

            _serviceClient = new ServiceClient(connectionString);

            if (!_serviceClient.IsReady)
            {
                Console.WriteLine($"Connection Error: {_serviceClient.LastError}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error connecting to Dataverse: {ex.Message}");
            return false;
        }
    }

    private static void InitializeHttpClient()
    {
        _httpClient = new HttpClient
        {
            BaseAddress = _serviceClient!.ConnectedOrgUriActual
        };
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _serviceClient.CurrentAccessToken);
        _httpClient.DefaultRequestHeaders.Add("OData-MaxVersion", "4.0");
        _httpClient.DefaultRequestHeaders.Add("OData-Version", "4.0");
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static async Task<List<Entity>> SelectSolutionsAsync(string? solutionNameFilter = null)
    {
        Console.WriteLine("Fetching solutions...");

        var query = new QueryExpression("solution")
        {
            ColumnSet = new ColumnSet("solutionid", "friendlyname", "uniquename", "version", "ismanaged"),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression("ismanaged", ConditionOperator.Equal, true),
                    new ConditionExpression("isvisible", ConditionOperator.Equal, true)
                }
            },
            Orders = { new OrderExpression("friendlyname", OrderType.Ascending) }
        };

        var solutions = await Task.Run(() => _serviceClient!.RetrieveMultiple(query));

        if (solutions.Entities.Count == 0)
        {
            Console.WriteLine("No managed solutions found.");
            return new List<Entity>();
        }

        // If solution name was provided via command line, find it automatically
        if (!string.IsNullOrWhiteSpace(solutionNameFilter))
        {
            // Support comma-delimited solution names from command line
            var filterNames = solutionNameFilter.Split(',').Select(s => s.Trim()).ToList();
            var matchingSolutions = new List<Entity>();

            foreach (var filterName in filterNames)
            {
                var matchingSolution = solutions.Entities.FirstOrDefault(s =>
                {
                    var friendlyName = s.GetAttributeValue<string>("friendlyname") ?? "";
                    var uniqueName = s.GetAttributeValue<string>("uniquename") ?? "";
                    return friendlyName.Equals(filterName, StringComparison.OrdinalIgnoreCase) ||
                           uniqueName.Equals(filterName, StringComparison.OrdinalIgnoreCase);
                });

                if (matchingSolution != null)
                {
                    var name = matchingSolution.GetAttributeValue<string>("friendlyname") ?? "Unknown";
                    Console.WriteLine($"Auto-selected solution: {name}");
                    matchingSolutions.Add(matchingSolution);
                }
                else
                {
                    Console.WriteLine($"Solution '{filterName}' not found.");
                }
            }

            if (matchingSolutions.Count > 0)
            {
                return matchingSolutions;
            }
            Console.WriteLine("No matching solutions found. Showing list...");
        }

        Console.WriteLine();
        Console.WriteLine("Available Managed Solutions:");
        Console.WriteLine("----------------------------");

        for (int i = 0; i < solutions.Entities.Count; i++)
        {
            var sol = solutions.Entities[i];
            string name = sol.GetAttributeValue<string>("friendlyname") ?? "Unknown";
            string uniqueName = sol.GetAttributeValue<string>("uniquename") ?? "Unknown";
            string version = sol.GetAttributeValue<string>("version") ?? "N/A";
            Console.WriteLine($"  {i + 1}. {name} ({uniqueName}) - v{version}");
        }

        Console.WriteLine();
        Console.WriteLine("Enter solution number(s) to check (comma-separated, e.g., 1,3,5) or 0 to exit:");
        Console.Write("> ");

        while (true)
        {
            string? input = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(input))
            {
                Console.Write("Please enter a selection: ");
                continue;
            }

            if (input == "0")
                return new List<Entity>();

            // Parse comma-delimited list of numbers
            var selectedSolutions = new List<Entity>();
            var parts = input.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s));
            bool validInput = true;

            foreach (var part in parts)
            {
                if (int.TryParse(part, out int selection))
                {
                    if (selection >= 1 && selection <= solutions.Entities.Count)
                    {
                        var selectedSolution = solutions.Entities[selection - 1];
                        if (!selectedSolutions.Contains(selectedSolution))
                        {
                            selectedSolutions.Add(selectedSolution);
                        }
                    }
                    else
                    {
                        Console.WriteLine($"  Invalid selection: {selection} (must be 1-{solutions.Entities.Count})");
                        validInput = false;
                    }
                }
                else
                {
                    Console.WriteLine($"  Invalid input: '{part}' is not a number");
                    validInput = false;
                }
            }

            if (validInput && selectedSolutions.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"Selected {selectedSolutions.Count} solution(s):");
                foreach (var sol in selectedSolutions)
                {
                    Console.WriteLine($"  - {sol.GetAttributeValue<string>("friendlyname")}");
                }
                return selectedSolutions;
            }

            Console.Write("Please enter valid solution number(s): ");
        }
    }

    private static async Task<List<ComponentResult>> ProcessSolutionComponentsAsync(Entity solution, bool exportOnly = false, bool showDiffs = false)
    {
        Guid solutionId = solution.GetAttributeValue<Guid>("solutionid");
        string solutionName = solution.GetAttributeValue<string>("friendlyname") ?? "Unknown";

        Console.WriteLine();
        Console.WriteLine($"Processing solution: {solutionName}");
        Console.WriteLine("Fetching solution components...");

        // Track all component results for export
        var componentResults = new List<ComponentResult>();

        // Get all components in the solution with paging support
        var allComponents = await RetrieveAllComponentsAsync(solutionId);

        Console.WriteLine($"Found {allComponents.Count} components in the solution.");

        // Debug: Show component type breakdown
        var componentTypeCounts = allComponents
            .GroupBy(c => c.GetAttributeValue<OptionSetValue>("componenttype")?.Value ?? 0)
            .OrderByDescending(g => g.Count())
            .Take(10);
        Console.WriteLine("[DEBUG] Top component types in solution:");
        foreach (var group in componentTypeCounts)
        {
            Console.WriteLine($"  - Type {group.Key} ({GetComponentTypeName(group.Key)}): {group.Count()}");
        }
        Console.WriteLine();

        // Separate Power Pages components from standard components
        var powerPagesComponentTypes = new HashSet<int> { 10295, 10296, 10297 };
        var standardComponents = allComponents
            .Where(c => !powerPagesComponentTypes.Contains(c.GetAttributeValue<OptionSetValue>("componenttype")?.Value ?? 0))
            .ToList();
        var powerPagesComponents = allComponents
            .Where(c => powerPagesComponentTypes.Contains(c.GetAttributeValue<OptionSetValue>("componenttype")?.Value ?? 0))
            .ToList();

        Console.WriteLine($"  Standard components: {standardComponents.Count}");
        Console.WriteLine($"  Power Pages components: {powerPagesComponents.Count}");
        Console.WriteLine();

        // Build entity metadata lookup for Entity components (type 1)
        // The RetrieveSolutionComponentLayers API requires the actual entity logical name, not "entity"
        var entityComponents = standardComponents
            .Where(c => c.GetAttributeValue<OptionSetValue>("componenttype")?.Value == 1)
            .ToList();

        Dictionary<Guid, string> entityMetadataMap = new();
        if (entityComponents.Count > 0)
        {
            Console.WriteLine($"Resolving entity names for {entityComponents.Count} entity components...");
            entityMetadataMap = await GetEntityMetadataMapAsync(entityComponents);
            Console.WriteLine($"  Resolved {entityMetadataMap.Count} entity names.");
        }

        // Check standard components for unmanaged layers using RetrieveSolutionComponentLayers
        var matchingLayers = new List<(Entity Component, Entity Layer, string LogicalName)>();
        if (standardComponents.Count > 0)
        {
            Console.WriteLine("Checking standard components for unmanaged layers...");
            matchingLayers = await GetUnmanagedLayersForComponentsAsync(standardComponents, entityMetadataMap);
            Console.WriteLine($"Found {matchingLayers.Count} standard components with unmanaged layers.");
        }

        // Check Power Pages components
        List<Entity> unmanagedPowerPagesComponents = new();
        if (powerPagesComponents.Count > 0)
        {
            Console.WriteLine($"Checking {powerPagesComponents.Count} Power Pages components for unmanaged customizations...");
            unmanagedPowerPagesComponents = await GetUnmanagedPowerPagesComponentsAsync(powerPagesComponents);
            Console.WriteLine($"Found {unmanagedPowerPagesComponents.Count} Power Pages components with unmanaged customizations.");
        }

        int totalUnmanagedCount = matchingLayers.Count + unmanagedPowerPagesComponents.Count;
        Console.WriteLine();

        if (totalUnmanagedCount == 0)
        {
            Console.WriteLine("No unmanaged layers found for this solution's components.");
            return componentResults;
        }

        int layersRemoved = 0;
        bool removeAll = false;
        bool skipAll = exportOnly; // If export only, skip all removals

        // Process standard component layers
        for (int i = 0; i < matchingLayers.Count; i++)
        {
            var (component, layer, logicalName) = matchingLayers[i];
            var componentType = component.GetAttributeValue<OptionSetValue>("componenttype")?.Value ?? 0;
            var componentName = layer.GetAttributeValue<string>("msdyn_name") ?? "Unknown";
            var componentId = layer.GetAttributeValue<string>("msdyn_componentid") ?? "";
            var entityName = component.GetAttributeValue<string>("_entityname") ?? "";
            var modifiedOn = layer.GetAttributeValue<DateTime?>("msdyn_overwritetime");
            var modifiedBy = layer.GetAttributeValue<string>("msdyn_publishername") ?? "Unknown";

            bool wasRemoved = false;
            string removalStatus = "Not Removed";

            if (exportOnly)
            {
                // Export only mode - just collect data, no prompts
                if (i == 0)
                {
                    Console.WriteLine($"Collecting {matchingLayers.Count} standard components for export...");
                }
            }
            else
            {
                DisplayLayerInfo(layer, componentType);

                if (skipAll)
                {
                    // Already skipping all - just record it
                    removalStatus = "Skipped (Skip All)";
                }
                else if (removeAll)
                {
                    Console.WriteLine("Auto-removing unmanaged layer...");
                    wasRemoved = await RemoveUnmanagedLayerAsync(layer, logicalName);
                    if (wasRemoved)
                    {
                        layersRemoved++;
                        removalStatus = "Removed";
                    }
                    else
                    {
                        removalStatus = "Removal Failed";
                    }
                    Console.WriteLine();
                }
                else
                {
                    Console.Write("Do you want to remove this unmanaged layer? (y/n/a=all/s=skip all): ");
                    string? response = Console.ReadLine()?.Trim().ToLower();

                    if (response == "s")
                    {
                        Console.WriteLine("Skipping remaining layers.");
                        skipAll = true;
                        removalStatus = "Skipped (Skip All)";
                    }
                    else if (response == "a")
                    {
                        removeAll = true;
                        wasRemoved = await RemoveUnmanagedLayerAsync(layer, logicalName);
                        if (wasRemoved)
                        {
                            layersRemoved++;
                            removalStatus = "Removed";
                        }
                        else
                        {
                            removalStatus = "Removal Failed";
                        }
                        Console.WriteLine();
                    }
                    else if (response == "y")
                    {
                        wasRemoved = await RemoveUnmanagedLayerAsync(layer, logicalName);
                        if (wasRemoved)
                        {
                            layersRemoved++;
                            removalStatus = "Removed";
                        }
                        else
                        {
                            removalStatus = "Removal Failed";
                        }
                    }
                    else
                    {
                        Console.WriteLine("Skipped.");
                        removalStatus = "Skipped";
                    }

                    Console.WriteLine();
                }
            }

            // Calculate diff details if requested
            string diffDetails = "";
            if (showDiffs)
            {
                var parsedId = Guid.TryParse(componentId, out var compId) ? compId : Guid.Empty;
                if (parsedId != Guid.Empty)
                {
                    diffDetails = await GetComponentDiffAsync(componentType, parsedId, entityName);
                }
            }

            // Track result for export
            componentResults.Add(new ComponentResult(
                ComponentName: componentName,
                ComponentType: GetComponentTypeName(componentType),
                ComponentId: Guid.TryParse(componentId, out var id) ? id : Guid.Empty,
                EntityName: entityName,
                SolutionLayer: "Active",
                ModifiedOn: modifiedOn,
                ModifiedBy: modifiedBy,
                WasRemoved: wasRemoved,
                RemovalStatus: removalStatus,
                DiffDetails: diffDetails
            ));
        }

        // Process Power Pages components
        int powerPagesRemoved = 0;
        if (unmanagedPowerPagesComponents.Count > 0 && !exportOnly && !skipAll)
        {
            if (!removeAll)
            {
                Console.WriteLine();
                Console.WriteLine("===========================================");
                Console.WriteLine("  POWER PAGES UNMANAGED CUSTOMIZATIONS");
                Console.WriteLine("===========================================");
            }
        }
        else if (unmanagedPowerPagesComponents.Count > 0 && exportOnly)
        {
            Console.WriteLine($"Collecting {unmanagedPowerPagesComponents.Count} Power Pages components for export...");
        }

        for (int i = 0; i < unmanagedPowerPagesComponents.Count; i++)
        {
            var ppComponent = unmanagedPowerPagesComponents[i];
            var componentName = ppComponent.GetAttributeValue<string>("name") ?? "Unknown";
            var ppComponentType = ppComponent.GetAttributeValue<OptionSetValue>("powerpagecomponenttype");
            var componentTypeName = ppComponentType != null ? GetPowerPagesComponentTypeName(ppComponentType.Value) : "Unknown";
            var componentId = ppComponent.GetAttributeValue<Guid>("powerpagecomponentid");
            var modifiedOn = ppComponent.GetAttributeValue<DateTime?>("modifiedon");
            var modifiedByRef = ppComponent.GetAttributeValue<EntityReference>("modifiedby");
            var modifiedBy = modifiedByRef?.Name ?? "Unknown";

            bool wasRemoved = false;
            string removalStatus = "Not Removed";

            if (exportOnly)
            {
                // Export only mode - just collect data
            }
            else if (skipAll)
            {
                removalStatus = "Skipped (Skip All)";
            }
            else
            {
                DisplayPowerPagesComponentInfo(ppComponent);

                if (removeAll)
                {
                    Console.WriteLine("Auto-removing Power Pages unmanaged customization...");
                    wasRemoved = await RemovePowerPagesUnmanagedCustomizationAsync(ppComponent);
                    if (wasRemoved)
                    {
                        powerPagesRemoved++;
                        removalStatus = "Removed";
                    }
                    else
                    {
                        removalStatus = "Removal Failed";
                    }
                    Console.WriteLine();
                }
                else
                {
                    Console.Write("Do you want to remove this unmanaged customization? (y/n/a=all/s=skip all): ");
                    string? response = Console.ReadLine()?.Trim().ToLower();

                    if (response == "s")
                    {
                        Console.WriteLine("Skipping remaining components.");
                        skipAll = true;
                        removalStatus = "Skipped (Skip All)";
                    }
                    else if (response == "a")
                    {
                        removeAll = true;
                        wasRemoved = await RemovePowerPagesUnmanagedCustomizationAsync(ppComponent);
                        if (wasRemoved)
                        {
                            powerPagesRemoved++;
                            removalStatus = "Removed";
                        }
                        else
                        {
                            removalStatus = "Removal Failed";
                        }
                        Console.WriteLine();
                    }
                    else if (response == "y")
                    {
                        wasRemoved = await RemovePowerPagesUnmanagedCustomizationAsync(ppComponent);
                        if (wasRemoved)
                        {
                            powerPagesRemoved++;
                            removalStatus = "Removed";
                        }
                        else
                        {
                            removalStatus = "Removal Failed";
                        }
                    }
                    else
                    {
                        Console.WriteLine("Skipped.");
                        removalStatus = "Skipped";
                    }

                    Console.WriteLine();
                }
            }

            // Calculate diff for Power Pages (limited - just note that it has unmanaged customizations)
            string diffDetails = "";
            if (showDiffs)
            {
                diffDetails = await GetPowerPagesDiffAsync(ppComponent);
            }

            // Track result for export
            componentResults.Add(new ComponentResult(
                ComponentName: componentName,
                ComponentType: $"Power Pages - {componentTypeName}",
                ComponentId: componentId,
                EntityName: "",
                SolutionLayer: "Active",
                ModifiedOn: modifiedOn,
                ModifiedBy: modifiedBy,
                WasRemoved: wasRemoved,
                RemovalStatus: removalStatus,
                DiffDetails: diffDetails
            ));
        }

        Console.WriteLine("-------------------------------------------");
        Console.WriteLine($"Summary:");
        Console.WriteLine($"  Standard components with unmanaged layers: {matchingLayers.Count}");
        Console.WriteLine($"  Power Pages components with unmanaged customizations: {unmanagedPowerPagesComponents.Count}");
        Console.WriteLine($"  Total unmanaged layers removed: {layersRemoved + powerPagesRemoved}");

        return componentResults;
    }

    private static async Task<List<Entity>> RetrieveAllComponentsAsync(Guid solutionId)
    {
        var allComponents = new List<Entity>();

        var componentQuery = new QueryExpression("solutioncomponent")
        {
            ColumnSet = new ColumnSet("componenttype", "objectid", "solutioncomponentid"),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression("solutionid", ConditionOperator.Equal, solutionId)
                }
            },
            PageInfo = new PagingInfo
            {
                Count = 5000,
                PageNumber = 1,
                ReturnTotalRecordCount = false
            }
        };

        int pageNumber = 1;
        while (true)
        {
            Console.Write($"\r  Fetching components... (page {pageNumber}, {allComponents.Count} found)    ");
            var results = await Task.Run(() => _serviceClient!.RetrieveMultiple(componentQuery));
            allComponents.AddRange(results.Entities);

            if (results.MoreRecords)
            {
                componentQuery.PageInfo.PageNumber++;
                componentQuery.PageInfo.PagingCookie = results.PagingCookie;
                pageNumber++;
            }
            else
            {
                break;
            }
        }
        Console.WriteLine($"\r  Fetching components... done ({allComponents.Count} total)          ");

        return allComponents;
    }

    private static async Task<Dictionary<Guid, string>> GetEntityMetadataMapAsync(List<Entity> entityComponents)
    {
        var entityMap = new Dictionary<Guid, string>();

        // Get all entity metadata IDs from the solution components
        var metadataIds = entityComponents
            .Select(c => c.GetAttributeValue<Guid>("objectid"))
            .Where(id => id != Guid.Empty)
            .ToHashSet();

        if (metadataIds.Count == 0)
            return entityMap;

        try
        {
            // Retrieve all entity metadata - this gives us MetadataId and LogicalName
            var request = new RetrieveAllEntitiesRequest
            {
                EntityFilters = EntityFilters.Entity,
                RetrieveAsIfPublished = false
            };

            var response = await Task.Run(() =>
                (RetrieveAllEntitiesResponse)_serviceClient!.Execute(request));

            foreach (var entityMetadata in response.EntityMetadata)
            {
                if (metadataIds.Contains(entityMetadata.MetadataId ?? Guid.Empty))
                {
                    entityMap[entityMetadata.MetadataId!.Value] = entityMetadata.LogicalName;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Warning: Could not retrieve entity metadata: {ex.Message}");
        }

        return entityMap;
    }

    private static async Task<List<(Entity Component, Entity Layer, string LogicalName)>> GetUnmanagedLayersForComponentsAsync(
        List<Entity> components, Dictionary<Guid, string> entityMetadataMap)
    {
        var results = new List<(Entity Component, Entity Layer, string LogicalName)>();

        // Use the same approach as Power Pages: check Active Solution for unmanaged customizations
        // Step 1: Find the "Active Solution"
        Console.Write("\r  Finding Active Solution...                              ");
        var activeSolutionQuery = new QueryExpression("solution")
        {
            ColumnSet = new ColumnSet("solutionid"),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression("uniquename", ConditionOperator.Equal, "Active")
                }
            },
            TopCount = 1
        };

        var activeSolutionResult = await Task.Run(() => _serviceClient!.RetrieveMultiple(activeSolutionQuery));
        if (activeSolutionResult.Entities.Count == 0)
        {
            Console.WriteLine("\r  Active Solution not found.                              ");
            return results;
        }

        var activeSolutionId = activeSolutionResult.Entities[0].GetAttributeValue<Guid>("solutionid");
        Console.WriteLine($"\r  Active Solution ID: {activeSolutionId}                   ");

        // Step 2: Get all components in the Active Solution (these are unmanaged customizations)
        Console.Write("\r  Fetching Active Solution components...                  ");
        var activeComponents = new HashSet<(Guid ObjectId, int ComponentType)>();

        var activeComponentQuery = new QueryExpression("solutioncomponent")
        {
            ColumnSet = new ColumnSet("objectid", "componenttype"),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression("solutionid", ConditionOperator.Equal, activeSolutionId)
                }
            },
            PageInfo = new PagingInfo { Count = 5000, PageNumber = 1 }
        };

        int pageNumber = 1;
        while (true)
        {
            Console.Write($"\r  Fetching Active Solution components... (page {pageNumber})    ");
            var activeResult = await Task.Run(() => _serviceClient!.RetrieveMultiple(activeComponentQuery));

            foreach (var comp in activeResult.Entities)
            {
                var objId = comp.GetAttributeValue<Guid>("objectid");
                var compType = comp.GetAttributeValue<OptionSetValue>("componenttype")?.Value ?? 0;
                if (objId != Guid.Empty)
                {
                    activeComponents.Add((objId, compType));
                }
            }

            if (activeResult.MoreRecords)
            {
                activeComponentQuery.PageInfo.PageNumber++;
                activeComponentQuery.PageInfo.PagingCookie = activeResult.PagingCookie;
                pageNumber++;
            }
            else
            {
                break;
            }
        }

        Console.WriteLine($"\r  Found {activeComponents.Count} components in Active Solution.          ");

        // Step 3: For entities in the managed solution, find their subcomponents in the Active Solution
        // The managed solution typically only lists entities (type 1), not individual forms/views/etc.
        // We need to find forms, views, attributes, etc. that belong to those entities AND are in Active Solution
        Console.Write("\r  Finding entity subcomponents with customizations...     ");

        var matchingComponents = new List<(Entity Component, int ComponentType, Guid ObjectId, string LogicalName)>();

        // Get the set of entity logical names from the managed solution
        var managedEntityNames = new HashSet<string>();
        foreach (var comp in components)
        {
            var compType = comp.GetAttributeValue<OptionSetValue>("componenttype")?.Value ?? 0;
            if (compType == 1) // Entity
            {
                var objectId = comp.GetAttributeValue<Guid>("objectid");
                if (entityMetadataMap.TryGetValue(objectId, out var entityName))
                {
                    managedEntityNames.Add(entityName);
                }
            }
        }

        Console.WriteLine($"\r  Found {managedEntityNames.Count} entities in managed solution.              ");

        // Also check for explicitly listed non-entity components (like standalone web resources, workflows)
        foreach (var component in components)
        {
            var objectId = component.GetAttributeValue<Guid>("objectid");
            var componentType = component.GetAttributeValue<OptionSetValue>("componenttype")?.Value ?? 0;

            if (objectId == Guid.Empty || componentType == 1)
                continue;

            // Check if this explicit component is in the Active Solution
            if (activeComponents.Contains((objectId, componentType)))
            {
                string componentLogicalName = GetSolutionComponentLogicalName(componentType) ?? "unknown";
                matchingComponents.Add((component, componentType, objectId, componentLogicalName));
            }
        }

        // Now find subcomponents (forms, views, etc.) in Active Solution that belong to our entities
        if (managedEntityNames.Count > 0)
        {
            // Get Active Solution subcomponents for our entities
            var entitySubcomponents = await GetActiveSubcomponentsForEntitiesAsync(
                activeSolutionId, managedEntityNames, activeComponents);
            matchingComponents.AddRange(entitySubcomponents);
        }

        Console.WriteLine($"\r  Found {matchingComponents.Count} components with unmanaged customizations.          ");

        // Step 4: Fetch display names for matching components
        if (matchingComponents.Count > 0)
        {
            Console.Write("\r  Fetching component details...                           ");
            var componentNames = await GetComponentDisplayNamesAsync(matchingComponents, entityMetadataMap);

            foreach (var (component, componentType, objectId, logicalName) in matchingComponents)
            {
                var displayName = componentNames.GetValueOrDefault(objectId, $"{GetComponentTypeName(componentType)} - {objectId}");

                // Create a pseudo-layer entity for display
                var activeLayer = new Entity("solutioncomponent");
                activeLayer["msdyn_solutionname"] = "Active";
                activeLayer["msdyn_componentid"] = objectId.ToString();
                activeLayer["msdyn_name"] = displayName;

                results.Add((component, activeLayer, logicalName));
            }
            Console.WriteLine($"\r  Component details fetched.                              ");
        }

        return results;
    }

    private static async Task<List<(Entity Component, int ComponentType, Guid ObjectId, string LogicalName)>> GetActiveSubcomponentsForEntitiesAsync(
        Guid activeSolutionId, HashSet<string> entityNames, HashSet<(Guid ObjectId, int ComponentType)> activeComponents)
    {
        var results = new List<(Entity Component, int ComponentType, Guid ObjectId, string LogicalName)>();

        // Query forms (type 60) in Active Solution that belong to our entities
        Console.Write("\r  Checking forms...                                        ");
        var forms = await GetEntitySubcomponentsAsync<Guid>(
            "systemform", "formid", "objecttypecode", "name",
            entityNames, activeComponents, 60);
        foreach (var (id, name, entityName) in forms)
        {
            var comp = new Entity("solutioncomponent");
            comp["objectid"] = id;
            comp["componenttype"] = new OptionSetValue(60);
            comp["_componentname"] = name;
            comp["_entityname"] = entityName;
            results.Add((comp, 60, id, "systemform"));
        }

        // Query views (type 26) in Active Solution that belong to our entities
        Console.Write("\r  Checking views...                                        ");
        var views = await GetEntitySubcomponentsAsync<Guid>(
            "savedquery", "savedqueryid", "returnedtypecode", "name",
            entityNames, activeComponents, 26);
        foreach (var (id, name, entityName) in views)
        {
            var comp = new Entity("solutioncomponent");
            comp["objectid"] = id;
            comp["componenttype"] = new OptionSetValue(26);
            comp["_componentname"] = name;
            comp["_entityname"] = entityName;
            results.Add((comp, 26, id, "savedquery"));
        }

        // Query charts (type 59) in Active Solution that belong to our entities
        Console.Write("\r  Checking charts...                                       ");
        var charts = await GetEntitySubcomponentsAsync<Guid>(
            "savedqueryvisualization", "savedqueryvisualizationid", "primaryentitytypecode", "name",
            entityNames, activeComponents, 59);
        foreach (var (id, name, entityName) in charts)
        {
            var comp = new Entity("solutioncomponent");
            comp["objectid"] = id;
            comp["componenttype"] = new OptionSetValue(59);
            comp["_componentname"] = name;
            comp["_entityname"] = entityName;
            results.Add((comp, 59, id, "savedqueryvisualization"));
        }

        // Query attributes (type 2) in Active Solution that belong to our entities
        Console.Write("\r  Checking attributes/columns...                           ");
        var attributes = await GetEntityAttributesInActiveAsync(entityNames, activeComponents);
        foreach (var (id, entityName, attributeName) in attributes)
        {
            var comp = new Entity("solutioncomponent");
            comp["objectid"] = id;
            comp["componenttype"] = new OptionSetValue(2);
            comp["_attributename"] = attributeName; // Store for display name lookup
            comp["_entityname"] = entityName;
            results.Add((comp, 2, id, "attribute"));
        }

        Console.WriteLine($"\r  Found {results.Count} entity subcomponents with customizations.          ");
        return results;
    }

    private static async Task<List<(Guid MetadataId, string EntityName, string AttributeName)>> GetEntityAttributesInActiveAsync(
        HashSet<string> entityNames, HashSet<(Guid ObjectId, int ComponentType)> activeComponents)
    {
        var results = new List<(Guid MetadataId, string EntityName, string AttributeName)>();

        try
        {
            int entityCount = 0;
            int totalEntities = entityNames.Count;

            foreach (var entityName in entityNames)
            {
                entityCount++;
                Console.Write($"\r  Checking attributes/columns... ({entityCount}/{totalEntities} entities)    ");

                // Retrieve entity metadata with attributes
                var request = new RetrieveEntityRequest
                {
                    LogicalName = entityName,
                    EntityFilters = EntityFilters.Attributes,
                    RetrieveAsIfPublished = false
                };

                try
                {
                    var response = await Task.Run(() =>
                        (RetrieveEntityResponse)_serviceClient!.Execute(request));

                    foreach (var attribute in response.EntityMetadata.Attributes)
                    {
                        if (attribute.MetadataId.HasValue)
                        {
                            // Check if this attribute's MetadataId is in the Active Solution
                            if (activeComponents.Contains((attribute.MetadataId.Value, 2)))
                            {
                                results.Add((attribute.MetadataId.Value, entityName, attribute.LogicalName));
                            }
                        }
                    }
                }
                catch
                {
                    // Skip entities that can't be retrieved (may not exist or be inaccessible)
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\r  Warning: Error checking attributes: {ex.Message}");
        }

        return results;
    }

    private static async Task<List<(Guid Id, string Name, string EntityName)>> GetEntitySubcomponentsAsync<T>(
        string tableName, string idColumn, string entityColumn, string nameColumn,
        HashSet<string> entityNames, HashSet<(Guid ObjectId, int ComponentType)> activeComponents, int componentType)
    {
        var results = new List<(Guid Id, string Name, string EntityName)>();

        try
        {
            // Query for components that belong to our entities
            var query = new QueryExpression(tableName)
            {
                ColumnSet = new ColumnSet(idColumn, entityColumn, nameColumn),
                Criteria = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression(entityColumn, ConditionOperator.In, entityNames.ToArray())
                    }
                },
                PageInfo = new PagingInfo { Count = 5000, PageNumber = 1 }
            };

            while (true)
            {
                var result = await Task.Run(() => _serviceClient!.RetrieveMultiple(query));

                foreach (var entity in result.Entities)
                {
                    var id = entity.GetAttributeValue<Guid>(idColumn);
                    var name = entity.GetAttributeValue<string>(nameColumn) ?? id.ToString();
                    var entityName = entity.GetAttributeValue<string>(entityColumn) ?? "Unknown";

                    // Check if this component is in the Active Solution
                    if (activeComponents.Contains((id, componentType)))
                    {
                        results.Add((id, name, entityName));
                    }
                }

                if (result.MoreRecords)
                {
                    query.PageInfo.PageNumber++;
                    query.PageInfo.PagingCookie = result.PagingCookie;
                }
                else
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            // Log but continue - some tables might not exist or be accessible
            Console.WriteLine($"\r  Warning: Could not query {tableName}: {ex.Message}");
        }

        return results;
    }

    private static async Task<Dictionary<Guid, string>> GetComponentDisplayNamesAsync(
        List<(Entity Component, int ComponentType, Guid ObjectId, string LogicalName)> components,
        Dictionary<Guid, string> entityMetadataMap)
    {
        var names = new Dictionary<Guid, string>();

        // Group components by type for efficient batch queries
        var byType = components.GroupBy(c => c.ComponentType);

        foreach (var group in byType)
        {
            var componentType = group.Key;
            var objectIds = group.Select(g => g.ObjectId).ToList();

            try
            {
                switch (componentType)
                {
                    case 1: // Entity - use metadata we already have
                        foreach (var id in objectIds)
                        {
                            if (entityMetadataMap.TryGetValue(id, out var entityName))
                                names[id] = $"Entity: {entityName}";
                        }
                        break;

                    case 2: // Attribute - use stored metadata from component
                        foreach (var item in group)
                        {
                            var attributeName = item.Component.GetAttributeValue<string>("_attributename") ?? "Unknown";
                            var entityName = item.Component.GetAttributeValue<string>("_entityname") ?? "Unknown";
                            names[item.ObjectId] = $"Attribute: {entityName}.{attributeName}";
                        }
                        break;

                    case 20: // Role
                        await FetchComponentNamesAsync("role", "roleid", "name", objectIds, names, "Role");
                        break;

                    case 26: // Saved Query (View) - use stored metadata if available
                        foreach (var item in group)
                        {
                            var componentName = item.Component.GetAttributeValue<string>("_componentname");
                            var entityName = item.Component.GetAttributeValue<string>("_entityname");
                            if (!string.IsNullOrEmpty(componentName) && !string.IsNullOrEmpty(entityName))
                            {
                                names[item.ObjectId] = $"View: {entityName}.{componentName}";
                            }
                            else
                            {
                                // Fall back to fetching from database
                                await FetchComponentNamesAsync("savedquery", "savedqueryid", "name", new List<Guid> { item.ObjectId }, names, "View");
                            }
                        }
                        break;

                    case 29: // Workflow
                        await FetchComponentNamesAsync("workflow", "workflowid", "name", objectIds, names, "Workflow");
                        break;

                    case 59: // Chart - use stored metadata if available
                        foreach (var item in group)
                        {
                            var componentName = item.Component.GetAttributeValue<string>("_componentname");
                            var entityName = item.Component.GetAttributeValue<string>("_entityname");
                            if (!string.IsNullOrEmpty(componentName) && !string.IsNullOrEmpty(entityName))
                            {
                                names[item.ObjectId] = $"Chart: {entityName}.{componentName}";
                            }
                            else
                            {
                                names[item.ObjectId] = $"Chart: {item.ObjectId}";
                            }
                        }
                        break;

                    case 60: // System Form - use stored metadata if available
                        foreach (var item in group)
                        {
                            var componentName = item.Component.GetAttributeValue<string>("_componentname");
                            var entityName = item.Component.GetAttributeValue<string>("_entityname");
                            if (!string.IsNullOrEmpty(componentName) && !string.IsNullOrEmpty(entityName))
                            {
                                names[item.ObjectId] = $"Form: {entityName}.{componentName}";
                            }
                            else
                            {
                                // Fall back to fetching from database
                                await FetchComponentNamesAsync("systemform", "formid", "name", new List<Guid> { item.ObjectId }, names, "Form");
                            }
                        }
                        break;

                    case 61: // Web Resource
                        await FetchComponentNamesAsync("webresource", "webresourceid", "name", objectIds, names, "Web Resource");
                        break;

                    case 62: // Site Map
                        await FetchComponentNamesAsync("sitemap", "sitemapid", "sitemapname", objectIds, names, "Site Map");
                        break;

                    case 80: // App Module
                        await FetchComponentNamesAsync("appmodule", "appmoduleid", "name", objectIds, names, "App");
                        break;

                    case 9: // Option Set
                        await FetchComponentNamesAsync("optionset", "optionsetid", "name", objectIds, names, "Option Set");
                        break;

                    case 300: // Canvas App
                        await FetchComponentNamesAsync("canvasapp", "canvasappid", "name", objectIds, names, "Canvas App");
                        break;

                    case 380: // Environment Variable Definition
                        await FetchComponentNamesAsync("environmentvariabledefinition", "environmentvariabledefinitionid", "displayname", objectIds, names, "Env Variable");
                        break;

                    default:
                        // For unknown types, just use the type name and ID
                        foreach (var id in objectIds)
                        {
                            names[id] = $"{GetComponentTypeName(componentType)}: {id}";
                        }
                        break;
                }
            }
            catch
            {
                // If query fails, fall back to generic names
                foreach (var id in objectIds)
                {
                    if (!names.ContainsKey(id))
                        names[id] = $"{GetComponentTypeName(componentType)}: {id}";
                }
            }
        }

        return names;
    }

    private static async Task FetchComponentNamesAsync(
        string tableName, string idColumn, string nameColumn, List<Guid> objectIds,
        Dictionary<Guid, string> names, string typePrefix)
    {
        if (objectIds.Count == 0) return;

        var query = new QueryExpression(tableName)
        {
            ColumnSet = new ColumnSet(idColumn, nameColumn),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression(idColumn, ConditionOperator.In, objectIds.Cast<object>().ToArray())
                }
            }
        };

        try
        {
            var result = await Task.Run(() => _serviceClient!.RetrieveMultiple(query));
            foreach (var entity in result.Entities)
            {
                var id = entity.GetAttributeValue<Guid>(idColumn);
                var name = entity.GetAttributeValue<string>(nameColumn) ?? id.ToString();
                names[id] = $"{typePrefix}: {name}";
            }
        }
        catch
        {
            // Silently fail - names will use fallback
        }

        // Add fallback for any IDs not found
        foreach (var id in objectIds)
        {
            if (!names.ContainsKey(id))
                names[id] = $"{typePrefix}: {id}";
        }
    }

    private static async Task<List<Entity>> GetUnmanagedPowerPagesComponentsAsync(List<Entity> powerPagesComponents)
    {
        var unmanagedComponents = new List<Entity>();

        try
        {
            // First, get the "Active Solution" ID - this is where unmanaged customizations are tracked
            Console.Write("\r  Finding Active Solution...                              ");
            var activeSolutionQuery = new QueryExpression("solution")
            {
                ColumnSet = new ColumnSet("solutionid"),
                Criteria = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression("friendlyname", ConditionOperator.Equal, "Active Solution")
                    }
                },
                TopCount = 1
            };

            var activeSolutionResult = await Task.Run(() => _serviceClient!.RetrieveMultiple(activeSolutionQuery));
            if (activeSolutionResult.Entities.Count == 0)
            {
                Console.WriteLine("\r  Active Solution not found.                              ");
                return unmanagedComponents;
            }

            var activeSolutionId = activeSolutionResult.Entities[0].GetAttributeValue<Guid>("solutionid");
            Console.WriteLine($"\r  Active Solution ID: {activeSolutionId}                   ");

            // Query the powerpagecomponent table directly
            // Components with solutionid = Active Solution AND ismanaged = true are unmanaged customizations
            Console.Write("\r  Querying powerpagecomponent table...                    ");

            var allPowerPageComponents = new List<Entity>();
            var ppQuery = new QueryExpression("powerpagecomponent")
            {
                ColumnSet = new ColumnSet("powerpagecomponentid", "name", "powerpagecomponenttype",
                    "solutionid", "ismanaged", "modifiedby", "modifiedon"),
                Criteria = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression("solutionid", ConditionOperator.Equal, activeSolutionId),
                        new ConditionExpression("ismanaged", ConditionOperator.Equal, true)
                    }
                },
                PageInfo = new PagingInfo { Count = 5000, PageNumber = 1 }
            };

            int pageNumber = 1;
            while (true)
            {
                Console.Write($"\r  Querying powerpagecomponent... (page {pageNumber}, {allPowerPageComponents.Count} found)    ");
                var results = await Task.Run(() => _serviceClient!.RetrieveMultiple(ppQuery));
                allPowerPageComponents.AddRange(results.Entities);

                if (results.MoreRecords)
                {
                    ppQuery.PageInfo.PageNumber++;
                    ppQuery.PageInfo.PagingCookie = results.PagingCookie;
                    pageNumber++;
                }
                else
                {
                    break;
                }
            }

            Console.WriteLine($"\r  Found {allPowerPageComponents.Count} Power Pages components with unmanaged layers.          ");

            // Get the component IDs from the managed solution we're checking
            var solutionComponentIds = new HashSet<Guid>(
                powerPagesComponents
                    .Select(c => c.GetAttributeValue<Guid>("objectid"))
                    .Where(id => id != Guid.Empty)
            );

            // Filter to only components that are in our target solution
            // Note: The powerpagecomponent records in Active Solution represent unmanaged customizations
            // We need to match them to the solution components we're looking at
            foreach (var ppComp in allPowerPageComponents)
            {
                var ppId = ppComp.GetAttributeValue<Guid>("powerpagecomponentid");

                // Check if this component is in our solution's component list
                if (solutionComponentIds.Contains(ppId))
                {
                    unmanagedComponents.Add(ppComp);
                }
            }

            // If no matches by ID, the unmanaged components might all be relevant for the solution
            // In that case, return all found components
            if (unmanagedComponents.Count == 0 && allPowerPageComponents.Count > 0)
            {
                Console.WriteLine($"  Note: Found {allPowerPageComponents.Count} total unmanaged Power Pages components in environment.");
                Console.WriteLine("  Showing all unmanaged Power Pages components (not filtered by solution):");
                unmanagedComponents = allPowerPageComponents;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"[DEBUG] Error checking Power Pages components: {ex.Message}");
        }

        return unmanagedComponents;
    }

    private static void DisplayPowerPagesComponentInfo(Entity ppComponent)
    {
        Console.WriteLine();
        Console.WriteLine("===========================================");
        Console.WriteLine("  POWER PAGES UNMANAGED CUSTOMIZATION");
        Console.WriteLine("===========================================");

        string componentName = ppComponent.GetAttributeValue<string>("name") ?? "Unknown";
        var componentType = ppComponent.GetAttributeValue<OptionSetValue>("powerpagecomponenttype");
        string componentTypeName = componentType != null ? GetPowerPagesComponentTypeName(componentType.Value) : "Unknown";
        DateTime? modifiedOn = ppComponent.GetAttributeValue<DateTime?>("modifiedon");
        var modifiedByRef = ppComponent.GetAttributeValue<EntityReference>("modifiedby");
        string modifiedBy = modifiedByRef?.Name ?? "Unknown";
        var componentId = ppComponent.GetAttributeValue<Guid>("powerpagecomponentid");

        Console.WriteLine($"  Component Name:  {componentName}");
        Console.WriteLine($"  Component ID:    {componentId}");
        Console.WriteLine($"  Component Type:  {componentTypeName}");
        Console.WriteLine($"  Modified On:     {modifiedOn?.ToString("yyyy-MM-dd HH:mm:ss") ?? "N/A"}");
        Console.WriteLine($"  Modified By:     {modifiedBy}");
        Console.WriteLine("-------------------------------------------");
    }

    private static string GetPowerPagesComponentTypeName(int componentType)
    {
        // Map based on Power Pages component types
        return componentType switch
        {
            1 => "Publishing State",
            2 => "Web Page",
            3 => "Web File",
            4 => "Web Link Set",
            5 => "Web Link",
            6 => "Page Template",
            7 => "Content Snippet",
            8 => "Web Template",
            9 => "Site Setting",
            10 => "Web Page Access Control Rule",
            11 => "Web Role",
            12 => "Website Access",
            13 => "Site Marker",
            15 => "Basic Form",
            16 => "Basic Form Metadata",
            17 => "List",
            18 => "Table Permission",
            19 => "Advanced Form",
            20 => "Advanced Form Step",
            21 => "Advanced Form Metadata",
            24 => "Poll Placement",
            26 => "Ad Placement",
            27 => "Bot Consumer",
            28 => "Column Permission Profile",
            29 => "Column Permission",
            30 => "Redirect",
            31 => "Publishing State Transition Rule",
            32 => "Shortcut",
            33 => "Cloud Flow",
            34 => "UX Component",
            _ => $"Type {componentType}"
        };
    }

    private static async Task<bool> RemovePowerPagesUnmanagedCustomizationAsync(Entity ppComponent)
    {
        try
        {
            var componentId = ppComponent.GetAttributeValue<Guid>("powerpagecomponentid");

            Console.WriteLine("Removing Power Pages unmanaged customization...");

            // Use RemoveActiveCustomization to remove the unmanaged layer
            // Note: The logical name is "powerpagecomponent" (not "powerpagesitecomponent")
            var request = new OrganizationRequest("RemoveActiveCustomization")
            {
                Parameters =
                {
                    { "LogicalName", "powerpagecomponent" },
                    { "Id", componentId }
                }
            };

            await Task.Run(() => _serviceClient!.Execute(request));

            Console.WriteLine("Power Pages unmanaged customization removed successfully!");
            return true;
        }
        catch (FaultException<OrganizationServiceFault> ex)
        {
            Console.WriteLine($"Error removing Power Pages customization: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            return false;
        }
    }

    private static void DisplayLayerInfo(Entity layer, int componentType)
    {
        Console.WriteLine();
        Console.WriteLine("===========================================");
        Console.WriteLine("  UNMANAGED LAYER FOUND");
        Console.WriteLine("===========================================");

        string componentName = layer.GetAttributeValue<string>("msdyn_name") ?? "Unknown";
        string componentTypeName = GetComponentTypeName(componentType);
        string solutionName = layer.GetAttributeValue<string>("msdyn_solutionname") ?? "Active";
        DateTime? modifiedOn = layer.GetAttributeValue<DateTime?>("msdyn_overwritetime");
        string modifiedBy = layer.GetAttributeValue<string>("msdyn_publishername") ?? "Unknown";
        int order = layer.GetAttributeValue<int>("msdyn_order");

        Console.WriteLine($"  Component Name:  {componentName}");
        Console.WriteLine($"  Component Type:  {componentTypeName}");
        Console.WriteLine($"  Solution Layer:  {solutionName}");
        Console.WriteLine($"  Layer Order:     {order}");
        Console.WriteLine($"  Modified On:     {modifiedOn?.ToString("yyyy-MM-dd HH:mm:ss") ?? "N/A"}");
        Console.WriteLine($"  Modified By:     {modifiedBy}");
        Console.WriteLine("-------------------------------------------");
    }

    private static async Task<bool> RemoveUnmanagedLayerAsync(Entity layer, string solutionComponentName)
    {
        try
        {
            string componentId = layer.GetAttributeValue<string>("msdyn_componentid") ?? "";

            if (string.IsNullOrEmpty(componentId) || !Guid.TryParse(componentId, out Guid objectId))
            {
                Console.WriteLine("Error: Invalid component ID.");
                return false;
            }

            Console.WriteLine("Removing unmanaged layer...");

            // Use RemoveActiveCustomizations to remove the unmanaged layer
            var request = new OrganizationRequest("RemoveActiveCustomizations")
            {
                Parameters =
                {
                    { "SolutionComponentName", solutionComponentName },
                    { "ComponentId", objectId }
                }
            };

            await Task.Run(() => _serviceClient!.Execute(request));

            Console.WriteLine("Unmanaged layer removed successfully!");
            return true;
        }
        catch (FaultException<OrganizationServiceFault> ex)
        {
            Console.WriteLine($"Error removing layer: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            return false;
        }
    }

    private static string GetComponentTypeName(int componentType)
    {
        return componentType switch
        {
            1 => "Entity",
            2 => "Attribute",
            3 => "Relationship",
            4 => "Attribute Picklist Value",
            5 => "Attribute Lookup Value",
            6 => "View Attribute",
            7 => "Localized Label",
            8 => "Relationship Extra Condition",
            9 => "Option Set",
            10 => "Entity Relationship",
            11 => "Entity Relationship Role",
            12 => "Entity Relationship Relationships",
            13 => "Managed Property",
            14 => "Entity Key",
            16 => "Privilege",
            17 => "Privilege Object Type Code",
            18 => "Index",
            20 => "Role",
            21 => "Role Privilege",
            22 => "Display String",
            23 => "Display String Map",
            24 => "Form",
            25 => "Organization",
            26 => "Saved Query",
            29 => "Workflow",
            31 => "Report",
            32 => "Report Entity",
            33 => "Report Category",
            34 => "Report Visibility",
            35 => "Attachment",
            36 => "Email Template",
            37 => "Contract Template",
            38 => "KB Article Template",
            39 => "Mail Merge Template",
            44 => "Duplicate Rule",
            45 => "Duplicate Rule Condition",
            46 => "Entity Map",
            47 => "Attribute Map",
            48 => "Ribbon Command",
            49 => "Ribbon Context Group",
            50 => "Ribbon Customization",
            52 => "Ribbon Rule",
            53 => "Ribbon Tab To Command Map",
            55 => "Ribbon Diff",
            59 => "Saved Query Visualization",
            60 => "System Form",
            61 => "Web Resource",
            62 => "Site Map",
            63 => "Connection Role",
            64 => "Complex Control",
            65 => "Hierarchy Rule",
            66 => "Custom Control",
            68 => "Custom Control Default Config",
            70 => "Field Security Profile",
            71 => "Field Permission",
            80 => "Model-driven App",
            90 => "Plugin Type",
            91 => "Plugin Assembly",
            92 => "SDK Message Processing Step",
            93 => "SDK Message Processing Step Image",
            95 => "Service Endpoint",
            150 => "Routing Rule",
            151 => "Routing Rule Item",
            152 => "SLA",
            153 => "SLA Item",
            154 => "Convert Rule",
            155 => "Convert Rule Item",
            161 => "Mobile Offline Profile",
            162 => "Mobile Offline Profile Item",
            165 => "Similarity Rule",
            166 => "Data Source Mapping",
            201 => "SDKMessage",
            202 => "SDKMessageFilter",
            203 => "SdkMessagePair",
            204 => "SdkMessageRequest",
            205 => "SdkMessageRequestField",
            206 => "SdkMessageResponse",
            207 => "SdkMessageResponseField",
            208 => "Import Map",
            210 => "WebWizard",
            300 => "Canvas App",
            371 => "Connector",
            372 => "Connector",
            380 => "Environment Variable Definition",
            381 => "Environment Variable Value",
            400 => "AI Project Type",
            401 => "AI Project",
            402 => "AI Configuration",
            430 => "Entity Analytics Configuration",
            431 => "Attribute Image Configuration",
            432 => "Entity Image Configuration",
            _ => $"Unknown ({componentType})"
        };
    }

    private static string? GetSolutionComponentLogicalName(int componentType)
    {
        // Comprehensive mapping of solution component types to their logical names
        // for use with RetrieveSolutionComponentLayers API
        return componentType switch
        {
            // Core entity components
            1 => "entity",
            2 => "attribute",
            3 => "relationship",
            9 => "optionset",
            10 => "entityrelationship",
            14 => "entitykey",

            // Security
            16 => "privilege",
            20 => "role",
            70 => "fieldsecurityprofile",
            71 => "fieldpermission",

            // UI Components
            24 => "systemform",
            26 => "savedquery",
            59 => "savedqueryvisualization",
            60 => "systemform",
            61 => "webresource",
            62 => "sitemap",
            63 => "connectionrole",
            64 => "complexcontrol",
            65 => "hierarchyrule",
            66 => "customcontrol",
            68 => "customcontroldefaultconfig",
            80 => "appmodule",

            // Business Logic
            29 => "workflow",
            44 => "duplicaterule",
            45 => "duplicaterulecondition",

            // Templates
            36 => "template",  // Email template
            37 => "contracttemplate",
            38 => "kbarticletemplate",
            39 => "mailmergetemplate",

            // Reports
            31 => "report",
            32 => "reportentity",
            33 => "reportcategory",
            34 => "reportvisibility",

            // Plugins and SDK
            90 => "plugintype",
            91 => "pluginassembly",
            92 => "sdkmessageprocessingstep",
            93 => "sdkmessageprocessingstepimage",
            95 => "serviceendpoint",

            // Mappings
            46 => "entitymap",
            47 => "attributemap",
            208 => "importmap",

            // Ribbon
            48 => "ribboncommand",
            49 => "ribboncontextgroup",
            50 => "ribboncustomization",
            52 => "ribbonrule",
            53 => "ribbontabtocommandmap",
            55 => "ribbondiff",

            // Service Management
            150 => "routingrule",
            151 => "routingruleitem",
            152 => "sla",
            153 => "slaitem",
            154 => "convertrule",
            155 => "convertruleitem",

            // Mobile
            161 => "mobileofflineprofile",
            162 => "mobileofflineprofileitem",

            // Similarity
            165 => "similarityrule",

            // Canvas Apps and Modern Components
            300 => "canvasapp",
            371 => "connector",
            372 => "connector",
            380 => "environmentvariabledefinition",
            381 => "environmentvariablevalue",

            // AI Components
            400 => "aiprojecttype",
            401 => "aiproject",
            402 => "aiconfiguration",

            // Analytics
            430 => "entityanalyticsconfiguration",
            431 => "attributeimageconfiguration",
            432 => "entityimageconfiguration",

            // SDK Messages (usually not customizable but included for completeness)
            201 => "sdkmessage",
            202 => "sdkmessagefilter",

            // Return null for unsupported/unknown types - they will be skipped
            _ => null
        };
    }

    private static void ExportResultsToExcel(Dictionary<string, List<ComponentResult>> allResults, string filePath)
    {
        try
        {
            // Ensure the directory exists
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var workbook = new XLWorkbook();

            // Characters not allowed in Excel worksheet names
            var invalidWorksheetChars = new[] { ':', '\\', '/', '?', '*', '[', ']' };

            // Create Summary worksheet first
            var summarySheet = workbook.Worksheets.Add("Summary");

            // Title
            summarySheet.Cell(1, 1).Value = "Unmanaged Customizations Report - Summary";
            summarySheet.Cell(1, 1).Style.Font.Bold = true;
            summarySheet.Cell(1, 1).Style.Font.FontSize = 16;
            summarySheet.Range(1, 1, 1, 5).Merge();

            summarySheet.Cell(2, 1).Value = $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            summarySheet.Range(2, 1, 2, 5).Merge();

            // Calculate overall totals
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
            summarySheet.Cell(11, 1).Value = "Breakdown by Solution";
            summarySheet.Cell(11, 1).Style.Font.Bold = true;
            summarySheet.Cell(11, 1).Style.Font.FontSize = 12;

            var solutionHeaders = new[] { "Solution", "Total", "Removed", "Skipped", "Failed" };
            for (int i = 0; i < solutionHeaders.Length; i++)
            {
                summarySheet.Cell(12, i + 1).Value = solutionHeaders[i];
                summarySheet.Cell(12, i + 1).Style.Font.Bold = true;
                summarySheet.Cell(12, i + 1).Style.Fill.BackgroundColor = XLColor.LightGray;
                summarySheet.Cell(12, i + 1).Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            }

            int solutionRow = 13;
            foreach (var (solutionName, results) in allResults)
            {
                int removed = results.Count(r => r.WasRemoved);
                int skipped = results.Count(r => !r.WasRemoved && r.RemovalStatus != "Removal Failed");
                int failed = results.Count(r => r.RemovalStatus == "Removal Failed");

                summarySheet.Cell(solutionRow, 1).Value = solutionName;
                summarySheet.Cell(solutionRow, 2).Value = results.Count;
                summarySheet.Cell(solutionRow, 3).Value = removed;
                summarySheet.Cell(solutionRow, 4).Value = skipped;
                summarySheet.Cell(solutionRow, 5).Value = failed;
                solutionRow++;
            }

            // Breakdown by Component Type
            int typeStartRow = solutionRow + 2;
            summarySheet.Cell(typeStartRow, 1).Value = "Breakdown by Component Type";
            summarySheet.Cell(typeStartRow, 1).Style.Font.Bold = true;
            summarySheet.Cell(typeStartRow, 1).Style.Font.FontSize = 12;

            var typeHeaders = new[] { "Component Type", "Total", "Removed", "Skipped", "Failed" };
            for (int i = 0; i < typeHeaders.Length; i++)
            {
                summarySheet.Cell(typeStartRow + 1, i + 1).Value = typeHeaders[i];
                summarySheet.Cell(typeStartRow + 1, i + 1).Style.Font.Bold = true;
                summarySheet.Cell(typeStartRow + 1, i + 1).Style.Fill.BackgroundColor = XLColor.LightGray;
                summarySheet.Cell(typeStartRow + 1, i + 1).Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            }

            var componentTypeGroups = allComponents
                .GroupBy(c => c.ComponentType)
                .OrderByDescending(g => g.Count());

            int typeRow = typeStartRow + 2;
            foreach (var group in componentTypeGroups)
            {
                int removed = group.Count(r => r.WasRemoved);
                int skipped = group.Count(r => !r.WasRemoved && r.RemovalStatus != "Removal Failed");
                int failed = group.Count(r => r.RemovalStatus == "Removal Failed");

                summarySheet.Cell(typeRow, 1).Value = group.Key;
                summarySheet.Cell(typeRow, 2).Value = group.Count();
                summarySheet.Cell(typeRow, 3).Value = removed;
                summarySheet.Cell(typeRow, 4).Value = skipped;
                summarySheet.Cell(typeRow, 5).Value = failed;
                typeRow++;
            }

            // Auto-fit columns on summary sheet
            summarySheet.Columns().AdjustToContents();

            // Create a single Details worksheet with all components
            var detailsSheet = workbook.Worksheets.Add("Details");

            // Check if any results have diff details
            bool hasDiffDetails = allResults.Values.SelectMany(r => r).Any(r => !string.IsNullOrEmpty(r.DiffDetails));

            // Add title
            detailsSheet.Cell(1, 1).Value = "Unmanaged Customizations Report - All Components";
            detailsSheet.Cell(1, 1).Style.Font.Bold = true;
            detailsSheet.Cell(1, 1).Style.Font.FontSize = 14;
            detailsSheet.Range(1, 1, 1, hasDiffDetails ? 10 : 9).Merge();

            detailsSheet.Cell(2, 1).Value = $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            detailsSheet.Range(2, 1, 2, hasDiffDetails ? 10 : 9).Merge();

            // Add headers (including Solution column and optionally Component Details)
            int headerRow = 4;
            var headersList = new List<string> { "Solution", "Component Name", "Component Type", "Component ID", "Entity", "Solution Layer", "Modified On", "Modified By", "Removal Status" };
            if (hasDiffDetails)
            {
                headersList.Add("Component Details");
            }
            var headers = headersList.ToArray();

            for (int i = 0; i < headers.Length; i++)
            {
                detailsSheet.Cell(headerRow, i + 1).Value = headers[i];
                detailsSheet.Cell(headerRow, i + 1).Style.Font.Bold = true;
                detailsSheet.Cell(headerRow, i + 1).Style.Fill.BackgroundColor = XLColor.LightGray;
                detailsSheet.Cell(headerRow, i + 1).Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            }

            // Add data from all solutions
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

                    // Add diff details if available
                    if (hasDiffDetails)
                    {
                        detailsSheet.Cell(dataRow, 10).Value = result.DiffDetails;
                    }

                    // Color-code the removal status
                    var statusCell = detailsSheet.Cell(dataRow, 9);
                    if (result.RemovalStatus == "Removed")
                    {
                        statusCell.Style.Fill.BackgroundColor = XLColor.LightGreen;
                    }
                    else if (result.RemovalStatus == "Removal Failed")
                    {
                        statusCell.Style.Fill.BackgroundColor = XLColor.LightCoral;
                    }
                    else
                    {
                        statusCell.Style.Fill.BackgroundColor = XLColor.LightYellow;
                    }

                    dataRow++;
                }
            }

            // Auto-fit columns on details sheet
            detailsSheet.Columns().AdjustToContents();

            // Set max width for Component Details column to avoid very wide columns
            if (hasDiffDetails)
            {
                var detailsColumn = detailsSheet.Column(10);
                if (detailsColumn.Width > 80)
                {
                    detailsColumn.Width = 80;
                    detailsColumn.Style.Alignment.WrapText = true;
                }
            }

            // Save the file
            workbook.SaveAs(filePath);

            Console.WriteLine();
            Console.WriteLine($"Results exported to: {Path.GetFullPath(filePath)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error exporting to Excel: {ex.Message}");
        }
    }

    // ============================================================
    // Layer Diff Calculation Methods
    // ============================================================

    private static async Task<string> GetComponentDiffAsync(int componentType, Guid componentId, string entityName)
    {
        try
        {
            return componentType switch
            {
                60 => await GetFormDiffAsync(componentId),      // System Form
                26 => await GetViewDiffAsync(componentId),      // Saved Query (View)
                59 => await GetChartDiffAsync(componentId),     // Chart
                2 => await GetAttributeDiffAsync(componentId, entityName),  // Attribute
                61 => await GetWebResourceDiffAsync(componentId), // Web Resource
                29 => await GetWorkflowDiffAsync(componentId),  // Workflow
                _ => "Diff not available for this component type"
            };
        }
        catch (Exception ex)
        {
            return $"Error calculating diff: {ex.Message}";
        }
    }

    private static async Task<string> GetFormDiffAsync(Guid formId)
    {
        try
        {
            // Retrieve the form with its XML
            var form = await Task.Run(() => _serviceClient!.Retrieve("systemform", formId,
                new ColumnSet("name", "formxml", "type", "objecttypecode", "ismanaged")));

            var formXml = form.GetAttributeValue<string>("formxml");
            var formName = form.GetAttributeValue<string>("name") ?? "Unknown";
            var entityName = form.GetAttributeValue<string>("objecttypecode") ?? "Unknown";
            var isManaged = form.GetAttributeValue<bool>("ismanaged");

            if (string.IsNullOrEmpty(formXml))
            {
                return "Form XML not available";
            }

            // Parse the form XML and extract key information
            var diffItems = new List<string>();

            try
            {
                var doc = XDocument.Parse(formXml);

                // Count tabs
                var tabs = doc.Descendants("tab").ToList();
                diffItems.Add($"Tabs: {tabs.Count}");

                // Count sections
                var sections = doc.Descendants("section").ToList();
                diffItems.Add($"Sections: {sections.Count}");

                // Count controls/fields
                var controls = doc.Descendants("control").ToList();
                diffItems.Add($"Controls: {controls.Count}");

                // List field names
                var fieldNames = controls
                    .Select(c => c.Attribute("datafieldname")?.Value)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Distinct()
                    .ToList();
                if (fieldNames.Count > 0)
                {
                    var displayFields = fieldNames.Take(10).ToList();
                    var fieldsStr = string.Join(", ", displayFields);
                    if (fieldNames.Count > 10)
                    {
                        fieldsStr += $" (+{fieldNames.Count - 10} more)";
                    }
                    diffItems.Add($"Fields: {fieldsStr}");
                }

                // Check for subgrids
                var subgrids = controls.Where(c => c.Attribute("classid")?.Value?.ToLower().Contains("subgrid") == true).ToList();
                if (subgrids.Count > 0)
                {
                    diffItems.Add($"Subgrids: {subgrids.Count}");
                }

                // Check for web resources
                var webResources = controls.Where(c => c.Attribute("classid")?.Value?.ToLower().Contains("webresource") == true).ToList();
                if (webResources.Count > 0)
                {
                    diffItems.Add($"Web Resources: {webResources.Count}");
                }

                // Check for business rules
                var events = doc.Descendants("event").ToList();
                if (events.Count > 0)
                {
                    diffItems.Add($"Events/Scripts: {events.Count}");
                }
            }
            catch
            {
                diffItems.Add("Could not parse form XML structure");
            }

            return string.Join("; ", diffItems);
        }
        catch (Exception ex)
        {
            return $"Error retrieving form: {ex.Message}";
        }
    }

    private static async Task<string> GetViewDiffAsync(Guid viewId)
    {
        try
        {
            // Retrieve the view with its XML
            var view = await Task.Run(() => _serviceClient!.Retrieve("savedquery", viewId,
                new ColumnSet("name", "fetchxml", "layoutxml", "returnedtypecode", "ismanaged")));

            var fetchXml = view.GetAttributeValue<string>("fetchxml");
            var layoutXml = view.GetAttributeValue<string>("layoutxml");
            var viewName = view.GetAttributeValue<string>("name") ?? "Unknown";

            var diffItems = new List<string>();

            // Parse FetchXML
            if (!string.IsNullOrEmpty(fetchXml))
            {
                try
                {
                    var fetchDoc = XDocument.Parse(fetchXml);

                    // Count attributes (columns in query)
                    var attributes = fetchDoc.Descendants("attribute").ToList();
                    diffItems.Add($"Query columns: {attributes.Count}");

                    // Count filters
                    var filters = fetchDoc.Descendants("filter").ToList();
                    var conditions = fetchDoc.Descendants("condition").ToList();
                    if (conditions.Count > 0)
                    {
                        diffItems.Add($"Filter conditions: {conditions.Count}");
                    }

                    // Count linked entities
                    var linkEntities = fetchDoc.Descendants("link-entity").ToList();
                    if (linkEntities.Count > 0)
                    {
                        diffItems.Add($"Linked entities: {linkEntities.Count}");
                    }

                    // Check for order by
                    var orderBy = fetchDoc.Descendants("order").ToList();
                    if (orderBy.Count > 0)
                    {
                        var orderFields = orderBy.Select(o => o.Attribute("attribute")?.Value).Where(a => a != null);
                        diffItems.Add($"Sort by: {string.Join(", ", orderFields)}");
                    }
                }
                catch
                {
                    diffItems.Add("Could not parse FetchXML");
                }
            }

            // Parse LayoutXML
            if (!string.IsNullOrEmpty(layoutXml))
            {
                try
                {
                    var layoutDoc = XDocument.Parse(layoutXml);

                    // Count visible columns
                    var cells = layoutDoc.Descendants("cell").ToList();
                    diffItems.Add($"Visible columns: {cells.Count}");

                    // List column names
                    var columnNames = cells
                        .Select(c => c.Attribute("name")?.Value)
                        .Where(n => !string.IsNullOrEmpty(n))
                        .ToList();
                    if (columnNames.Count > 0)
                    {
                        var displayCols = columnNames.Take(8).ToList();
                        var colsStr = string.Join(", ", displayCols);
                        if (columnNames.Count > 8)
                        {
                            colsStr += $" (+{columnNames.Count - 8} more)";
                        }
                        diffItems.Add($"Columns: {colsStr}");
                    }
                }
                catch
                {
                    diffItems.Add("Could not parse LayoutXML");
                }
            }

            return diffItems.Count > 0 ? string.Join("; ", diffItems) : "No view details available";
        }
        catch (Exception ex)
        {
            return $"Error retrieving view: {ex.Message}";
        }
    }

    private static async Task<string> GetChartDiffAsync(Guid chartId)
    {
        try
        {
            var chart = await Task.Run(() => _serviceClient!.Retrieve("savedqueryvisualization", chartId,
                new ColumnSet("name", "datadescription", "presentationdescription", "primaryentitytypecode")));

            var dataDesc = chart.GetAttributeValue<string>("datadescription");
            var presDesc = chart.GetAttributeValue<string>("presentationdescription");
            var chartName = chart.GetAttributeValue<string>("name") ?? "Unknown";

            var diffItems = new List<string>();

            // Parse data description
            if (!string.IsNullOrEmpty(dataDesc))
            {
                try
                {
                    var dataDoc = XDocument.Parse(dataDesc);
                    var measures = dataDoc.Descendants("measure").ToList();
                    var categories = dataDoc.Descendants("category").ToList();

                    if (measures.Count > 0)
                    {
                        diffItems.Add($"Measures: {measures.Count}");
                    }
                    if (categories.Count > 0)
                    {
                        var catAliases = categories.Select(c => c.Attribute("alias")?.Value).Where(a => a != null);
                        diffItems.Add($"Categories: {string.Join(", ", catAliases)}");
                    }
                }
                catch
                {
                    diffItems.Add("Could not parse chart data description");
                }
            }

            // Parse presentation description for chart type
            if (!string.IsNullOrEmpty(presDesc))
            {
                try
                {
                    var presDoc = XDocument.Parse(presDesc);
                    var chartType = presDoc.Descendants("Chart").FirstOrDefault()?.Element("Series")?.Element("Series")?.Attribute("ChartType")?.Value;
                    if (!string.IsNullOrEmpty(chartType))
                    {
                        diffItems.Add($"Chart type: {chartType}");
                    }
                }
                catch
                {
                    // Ignore presentation parsing errors
                }
            }

            return diffItems.Count > 0 ? string.Join("; ", diffItems) : "Chart visualization";
        }
        catch (Exception ex)
        {
            return $"Error retrieving chart: {ex.Message}";
        }
    }

    private static async Task<string> GetAttributeDiffAsync(Guid attributeMetadataId, string entityName)
    {
        try
        {
            if (string.IsNullOrEmpty(entityName))
            {
                return "Entity name not available for attribute diff";
            }

            // Retrieve entity metadata with attributes to find this specific attribute
            var request = new RetrieveEntityRequest
            {
                LogicalName = entityName,
                EntityFilters = EntityFilters.Attributes,
                RetrieveAsIfPublished = false
            };

            var response = await Task.Run(() => (RetrieveEntityResponse)_serviceClient!.Execute(request));

            var attribute = response.EntityMetadata.Attributes
                .FirstOrDefault(a => a.MetadataId == attributeMetadataId);

            if (attribute == null)
            {
                return "Attribute not found in entity metadata";
            }

            var diffItems = new List<string>();

            // Basic info
            diffItems.Add($"Logical name: {attribute.LogicalName}");
            diffItems.Add($"Type: {attribute.AttributeType}");

            // Display name
            if (attribute.DisplayName?.UserLocalizedLabel?.Label != null)
            {
                diffItems.Add($"Display: {attribute.DisplayName.UserLocalizedLabel.Label}");
            }

            // Requirement level
            if (attribute.RequiredLevel?.Value != null)
            {
                diffItems.Add($"Required: {attribute.RequiredLevel.Value}");
            }

            // Type-specific info
            switch (attribute)
            {
                case StringAttributeMetadata strAttr:
                    diffItems.Add($"Max length: {strAttr.MaxLength}");
                    if (strAttr.Format != null)
                        diffItems.Add($"Format: {strAttr.Format}");
                    break;

                case IntegerAttributeMetadata intAttr:
                    diffItems.Add($"Range: {intAttr.MinValue} - {intAttr.MaxValue}");
                    break;

                case DecimalAttributeMetadata decAttr:
                    diffItems.Add($"Precision: {decAttr.Precision}");
                    diffItems.Add($"Range: {decAttr.MinValue} - {decAttr.MaxValue}");
                    break;

                case MoneyAttributeMetadata moneyAttr:
                    diffItems.Add($"Precision: {moneyAttr.Precision}");
                    break;

                case PicklistAttributeMetadata picklistAttr:
                    var optionCount = picklistAttr.OptionSet?.Options?.Count ?? 0;
                    diffItems.Add($"Options: {optionCount}");
                    break;

                case LookupAttributeMetadata lookupAttr:
                    var targets = lookupAttr.Targets != null ? string.Join(", ", lookupAttr.Targets) : "Unknown";
                    diffItems.Add($"Targets: {targets}");
                    break;

                case DateTimeAttributeMetadata dateAttr:
                    if (dateAttr.Format != null)
                        diffItems.Add($"Format: {dateAttr.Format}");
                    break;
            }

            return string.Join("; ", diffItems);
        }
        catch (Exception ex)
        {
            return $"Error retrieving attribute: {ex.Message}";
        }
    }

    private static async Task<string> GetWebResourceDiffAsync(Guid webResourceId)
    {
        try
        {
            var webResource = await Task.Run(() => _serviceClient!.Retrieve("webresource", webResourceId,
                new ColumnSet("name", "webresourcetype", "displayname", "description")));

            var name = webResource.GetAttributeValue<string>("name") ?? "Unknown";
            var displayName = webResource.GetAttributeValue<string>("displayname");
            var webResourceType = webResource.GetAttributeValue<OptionSetValue>("webresourcetype")?.Value ?? 0;

            var typeName = webResourceType switch
            {
                1 => "HTML",
                2 => "CSS",
                3 => "JavaScript",
                4 => "XML",
                5 => "PNG",
                6 => "JPG",
                7 => "GIF",
                8 => "Silverlight (XAP)",
                9 => "Stylesheet (XSL)",
                10 => "ICO",
                11 => "Vector (SVG)",
                12 => "RESX",
                _ => $"Type {webResourceType}"
            };

            var diffItems = new List<string>
            {
                $"Name: {name}",
                $"Type: {typeName}"
            };

            if (!string.IsNullOrEmpty(displayName))
            {
                diffItems.Add($"Display: {displayName}");
            }

            return string.Join("; ", diffItems);
        }
        catch (Exception ex)
        {
            return $"Error retrieving web resource: {ex.Message}";
        }
    }

    private static async Task<string> GetWorkflowDiffAsync(Guid workflowId)
    {
        try
        {
            var workflow = await Task.Run(() => _serviceClient!.Retrieve("workflow", workflowId,
                new ColumnSet("name", "category", "type", "scope", "mode", "primaryentity", "triggeroncreate", "triggeronupdate", "triggerondelete")));

            var name = workflow.GetAttributeValue<string>("name") ?? "Unknown";
            var category = workflow.GetAttributeValue<OptionSetValue>("category")?.Value ?? 0;
            var type = workflow.GetAttributeValue<OptionSetValue>("type")?.Value ?? 0;
            var scope = workflow.GetAttributeValue<OptionSetValue>("scope")?.Value ?? 0;
            var mode = workflow.GetAttributeValue<OptionSetValue>("mode")?.Value ?? 0;
            var primaryEntity = workflow.GetAttributeValue<string>("primaryentity") ?? "None";

            var categoryName = category switch
            {
                0 => "Workflow",
                1 => "Dialog",
                2 => "Business Rule",
                3 => "Action",
                4 => "Business Process Flow",
                5 => "Modern Flow",
                6 => "Desktop Flow",
                _ => $"Category {category}"
            };

            var scopeName = scope switch
            {
                1 => "User",
                2 => "Business Unit",
                3 => "Parent-Child Business Units",
                4 => "Organization",
                _ => $"Scope {scope}"
            };

            var modeName = mode switch
            {
                0 => "Background",
                1 => "Real-time",
                _ => $"Mode {mode}"
            };

            var diffItems = new List<string>
            {
                $"Category: {categoryName}",
                $"Entity: {primaryEntity}",
                $"Scope: {scopeName}",
                $"Mode: {modeName}"
            };

            // Add trigger info
            var triggers = new List<string>();
            if (workflow.GetAttributeValue<bool>("triggeroncreate"))
                triggers.Add("Create");
            if (workflow.GetAttributeValue<bool>("triggeronupdate"))
                triggers.Add("Update");
            if (workflow.GetAttributeValue<bool>("triggerondelete"))
                triggers.Add("Delete");

            if (triggers.Count > 0)
            {
                diffItems.Add($"Triggers: {string.Join(", ", triggers)}");
            }

            return string.Join("; ", diffItems);
        }
        catch (Exception ex)
        {
            return $"Error retrieving workflow: {ex.Message}";
        }
    }

    private static async Task<string> GetPowerPagesDiffAsync(Entity ppComponent)
    {
        try
        {
            var componentName = ppComponent.GetAttributeValue<string>("name") ?? "Unknown";
            var componentType = ppComponent.GetAttributeValue<OptionSetValue>("powerpagecomponenttype")?.Value ?? 0;
            var componentTypeName = GetPowerPagesComponentTypeName(componentType);

            // For Power Pages, we can note that the component has been customized
            // More detailed diff would require component-specific content analysis
            var diffItems = new List<string>
            {
                $"Type: {componentTypeName}",
                "Has unmanaged customizations in Active Solution"
            };

            // Check if the component has content we can analyze
            var content = ppComponent.GetAttributeValue<string>("content");
            if (!string.IsNullOrEmpty(content))
            {
                // Estimate content size
                var sizeKb = content.Length / 1024.0;
                diffItems.Add($"Content size: {sizeKb:F1} KB");
            }

            return await Task.FromResult(string.Join("; ", diffItems));
        }
        catch (Exception ex)
        {
            return $"Error analyzing Power Pages component: {ex.Message}";
        }
    }
}
