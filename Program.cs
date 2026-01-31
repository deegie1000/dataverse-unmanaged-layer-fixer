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
            // Get the logical name for the component type
            var componentLogicalName = GetSolutionComponentLogicalName(componentType);
            if (string.IsNullOrEmpty(componentLogicalName))
            {
                // Fall back to component-specific diff for unsupported component types
                return await GetComponentSpecificDiffAsync(componentType, componentId, entityName, "managed");
            }

            // Use the API to retrieve solution component layers
            var layers = await GetComponentLayersAsync(componentId, componentLogicalName);

            if (layers == null || layers.Count == 0)
            {
                // Layers API didn't return data - fall back to component-specific comparison
                return await GetComponentSpecificDiffAsync(componentType, componentId, entityName, "managed");
            }

            // Find the Active (unmanaged) layer and the managed layer below it
            var activeLayer = layers.FirstOrDefault(l =>
                l.GetAttributeValue<string>("msdyn_solutionname")?.Equals("Active", StringComparison.OrdinalIgnoreCase) == true);

            var managedLayer = layers
                .Where(l => l.GetAttributeValue<string>("msdyn_solutionname")?.Equals("Active", StringComparison.OrdinalIgnoreCase) != true)
                .OrderByDescending(l => l.GetAttributeValue<int>("msdyn_order"))
                .FirstOrDefault();

            if (activeLayer == null)
            {
                // No active layer found but component is in Active Solution - may be a data issue
                return await GetComponentSpecificDiffAsync(componentType, componentId, entityName,
                    managedLayer?.GetAttributeValue<string>("msdyn_solutionname") ?? "managed");
            }

            if (managedLayer == null)
            {
                return "No managed layer to compare (component only exists in Active)";
            }

            var managedSolutionName = managedLayer.GetAttributeValue<string>("msdyn_solutionname") ?? "Unknown";

            // Get the component JSON from both layers
            var activeJson = activeLayer.GetAttributeValue<string>("msdyn_componentjson");
            var managedJson = managedLayer.GetAttributeValue<string>("msdyn_componentjson");

            if (string.IsNullOrEmpty(activeJson) && string.IsNullOrEmpty(managedJson))
            {
                // Fall back to component-specific comparison
                return await GetComponentSpecificDiffAsync(componentType, componentId, entityName, managedSolutionName);
            }

            // Compare the JSON and extract differences
            return CompareLayerJson(activeJson, managedJson, componentType, managedSolutionName);
        }
        catch (Exception ex)
        {
            // On any error, try component-specific diff as fallback
            try
            {
                return await GetComponentSpecificDiffAsync(componentType, componentId, entityName, "managed");
            }
            catch
            {
                return $"Error calculating diff: {ex.Message}";
            }
        }
    }

    private static async Task<List<Entity>> GetComponentLayersAsync(Guid componentId, string componentLogicalName)
    {
        // Use RetrieveSolutionComponentLayers API - msdyn_componentlayer is a virtual entity
        // that cannot be queried directly via QueryExpression
        try
        {
            var request = new OrganizationRequest("RetrieveSolutionComponentLayers")
            {
                Parameters =
                {
                    { "ComponentId", componentId },
                    { "SolutionComponentName", componentLogicalName }
                }
            };

            var response = await Task.Run(() => _serviceClient!.Execute(request));

            // The response contains SolutionComponentLayers (not just "Layers")
            if (response.Results.TryGetValue("SolutionComponentLayers", out var layersObj) && layersObj is EntityCollection layers)
            {
                return layers.Entities.ToList();
            }

            // Try alternative response key
            if (response.Results.TryGetValue("Layers", out layersObj) && layersObj is EntityCollection layers2)
            {
                return layers2.Entities.ToList();
            }

            return new List<Entity>();
        }
        catch
        {
            return new List<Entity>();
        }
    }

    private static async Task<List<Entity>> GetLayersViaApiAsync(Guid componentId, string componentLogicalName)
    {
        // This is now just an alias for GetComponentLayersAsync for backward compatibility
        return await GetComponentLayersAsync(componentId, componentLogicalName);
    }

    private static string CompareLayerJson(string? activeJson, string? managedJson, int componentType, string managedSolutionName)
    {
        var differences = new List<string>();
        differences.Add($"[vs {managedSolutionName}]");

        try
        {
            if (string.IsNullOrEmpty(activeJson))
            {
                return $"[vs {managedSolutionName}] Active layer has no JSON content";
            }

            if (string.IsNullOrEmpty(managedJson))
            {
                return $"[vs {managedSolutionName}] Managed layer has no JSON content - component may be new in Active";
            }

            // Parse both JSON documents
            using var activeDoc = JsonDocument.Parse(activeJson);
            using var managedDoc = JsonDocument.Parse(managedJson);

            var activeRoot = activeDoc.RootElement;
            var managedRoot = managedDoc.RootElement;

            // Component-type specific comparison
            switch (componentType)
            {
                case 60: // System Form
                    differences.AddRange(CompareFormLayers(activeRoot, managedRoot));
                    break;
                case 26: // Saved Query (View)
                    differences.AddRange(CompareViewLayers(activeRoot, managedRoot));
                    break;
                case 59: // Chart
                    differences.AddRange(CompareChartLayers(activeRoot, managedRoot));
                    break;
                default:
                    differences.AddRange(CompareGenericJson(activeRoot, managedRoot));
                    break;
            }

            if (differences.Count == 1) // Only has the header
            {
                differences.Add("No differences detected in layer content");
            }
        }
        catch (JsonException)
        {
            // JSON might actually be XML for some components
            differences.AddRange(CompareXmlContent(activeJson, managedJson, componentType));
        }
        catch (Exception ex)
        {
            differences.Add($"Comparison error: {ex.Message}");
        }

        return string.Join("; ", differences);
    }

    private static List<string> CompareFormLayers(JsonElement active, JsonElement managed)
    {
        var diffs = new List<string>();

        try
        {
            // Compare tabs
            var activeTabs = GetJsonArrayCount(active, "Tabs");
            var managedTabs = GetJsonArrayCount(managed, "Tabs");
            if (activeTabs != managedTabs)
            {
                diffs.Add($"Tabs: {managedTabs} → {activeTabs}");
            }

            // Compare controls
            var activeControls = CountNestedElements(active, "Controls");
            var managedControls = CountNestedElements(managed, "Controls");
            if (activeControls != managedControls)
            {
                diffs.Add($"Controls: {managedControls} → {activeControls}");
            }

            // Look for formxml if present
            if (TryGetString(active, "formxml", out var activeFormXml) &&
                TryGetString(managed, "formxml", out var managedFormXml))
            {
                diffs.AddRange(CompareFormXml(activeFormXml, managedFormXml));
            }

            // Check header/footer visibility changes
            CompareJsonProperty(active, managed, "HeaderVisible", diffs);
            CompareJsonProperty(active, managed, "FooterVisible", diffs);

            // Compare events/handlers
            var activeEvents = CountNestedElements(active, "Events");
            var managedEvents = CountNestedElements(managed, "Events");
            if (activeEvents != managedEvents)
            {
                diffs.Add($"Events: {managedEvents} → {activeEvents}");
            }
        }
        catch
        {
            diffs.Add("Could not fully parse form structure");
        }

        return diffs;
    }

    private static List<string> CompareFormXml(string activeXml, string managedXml)
    {
        var diffs = new List<string>();

        try
        {
            var activeDoc = XDocument.Parse(activeXml);
            var managedDoc = XDocument.Parse(managedXml);

            // Compare tabs
            var activeTabs = activeDoc.Descendants("tab").Select(t => t.Attribute("name")?.Value).Where(n => n != null).ToHashSet();
            var managedTabs = managedDoc.Descendants("tab").Select(t => t.Attribute("name")?.Value).Where(n => n != null).ToHashSet();

            var addedTabs = activeTabs.Except(managedTabs).ToList();
            var removedTabs = managedTabs.Except(activeTabs).ToList();

            if (addedTabs.Count > 0)
                diffs.Add($"+Tabs: {string.Join(", ", addedTabs.Take(3))}{(addedTabs.Count > 3 ? $" (+{addedTabs.Count - 3} more)" : "")}");
            if (removedTabs.Count > 0)
                diffs.Add($"-Tabs: {string.Join(", ", removedTabs.Take(3))}{(removedTabs.Count > 3 ? $" (+{removedTabs.Count - 3} more)" : "")}");

            // Compare sections
            var activeSections = activeDoc.Descendants("section").Select(s => s.Attribute("name")?.Value).Where(n => n != null).ToHashSet();
            var managedSections = managedDoc.Descendants("section").Select(s => s.Attribute("name")?.Value).Where(n => n != null).ToHashSet();

            var addedSections = activeSections.Except(managedSections).ToList();
            var removedSections = managedSections.Except(activeSections).ToList();

            if (addedSections.Count > 0)
                diffs.Add($"+Sections: {addedSections.Count}");
            if (removedSections.Count > 0)
                diffs.Add($"-Sections: {removedSections.Count}");

            // Compare fields/controls
            var activeFields = activeDoc.Descendants("control")
                .Select(c => c.Attribute("datafieldname")?.Value ?? c.Attribute("id")?.Value)
                .Where(n => n != null).ToHashSet();
            var managedFields = managedDoc.Descendants("control")
                .Select(c => c.Attribute("datafieldname")?.Value ?? c.Attribute("id")?.Value)
                .Where(n => n != null).ToHashSet();

            var addedFields = activeFields.Except(managedFields).ToList();
            var removedFields = managedFields.Except(activeFields).ToList();

            if (addedFields.Count > 0)
                diffs.Add($"+Fields: {string.Join(", ", addedFields.Take(5))}{(addedFields.Count > 5 ? $" (+{addedFields.Count - 5} more)" : "")}");
            if (removedFields.Count > 0)
                diffs.Add($"-Fields: {string.Join(", ", removedFields.Take(5))}{(removedFields.Count > 5 ? $" (+{removedFields.Count - 5} more)" : "")}");
        }
        catch
        {
            // XML parsing failed
        }

        return diffs;
    }

    private static List<string> CompareViewLayers(JsonElement active, JsonElement managed)
    {
        var diffs = new List<string>();

        try
        {
            // Compare fetchxml if present
            if (TryGetString(active, "fetchxml", out var activeFetch) &&
                TryGetString(managed, "fetchxml", out var managedFetch))
            {
                diffs.AddRange(CompareFetchXml(activeFetch, managedFetch));
            }

            // Compare layoutxml if present
            if (TryGetString(active, "layoutxml", out var activeLayout) &&
                TryGetString(managed, "layoutxml", out var managedLayout))
            {
                diffs.AddRange(CompareLayoutXml(activeLayout, managedLayout));
            }

            // Compare column count
            var activeColumns = GetJsonArrayCount(active, "Columns");
            var managedColumns = GetJsonArrayCount(managed, "Columns");
            if (activeColumns != managedColumns && (activeColumns > 0 || managedColumns > 0))
            {
                diffs.Add($"Columns: {managedColumns} → {activeColumns}");
            }
        }
        catch
        {
            diffs.Add("Could not fully parse view structure");
        }

        return diffs;
    }

    private static List<string> CompareFetchXml(string activeFetch, string managedFetch)
    {
        var diffs = new List<string>();

        try
        {
            var activeDoc = XDocument.Parse(activeFetch);
            var managedDoc = XDocument.Parse(managedFetch);

            // Compare query columns
            var activeAttrs = activeDoc.Descendants("attribute").Select(a => a.Attribute("name")?.Value).Where(n => n != null).ToHashSet();
            var managedAttrs = managedDoc.Descendants("attribute").Select(a => a.Attribute("name")?.Value).Where(n => n != null).ToHashSet();

            var addedAttrs = activeAttrs.Except(managedAttrs).ToList();
            var removedAttrs = managedAttrs.Except(activeAttrs).ToList();

            if (addedAttrs.Count > 0)
                diffs.Add($"+Query cols: {string.Join(", ", addedAttrs.Take(4))}{(addedAttrs.Count > 4 ? $" (+{addedAttrs.Count - 4})" : "")}");
            if (removedAttrs.Count > 0)
                diffs.Add($"-Query cols: {string.Join(", ", removedAttrs.Take(4))}{(removedAttrs.Count > 4 ? $" (+{removedAttrs.Count - 4})" : "")}");

            // Compare filter conditions
            var activeConditions = activeDoc.Descendants("condition").Count();
            var managedConditions = managedDoc.Descendants("condition").Count();
            if (activeConditions != managedConditions)
            {
                diffs.Add($"Filters: {managedConditions} → {activeConditions}");
            }

            // Compare linked entities
            var activeLinks = activeDoc.Descendants("link-entity").Select(l => l.Attribute("name")?.Value).Where(n => n != null).ToHashSet();
            var managedLinks = managedDoc.Descendants("link-entity").Select(l => l.Attribute("name")?.Value).Where(n => n != null).ToHashSet();

            if (!activeLinks.SetEquals(managedLinks))
            {
                var added = activeLinks.Except(managedLinks).ToList();
                var removed = managedLinks.Except(activeLinks).ToList();
                if (added.Count > 0) diffs.Add($"+Links: {string.Join(", ", added)}");
                if (removed.Count > 0) diffs.Add($"-Links: {string.Join(", ", removed)}");
            }

            // Compare sort order
            var activeOrder = activeDoc.Descendants("order").Select(o => o.Attribute("attribute")?.Value).FirstOrDefault();
            var managedOrder = managedDoc.Descendants("order").Select(o => o.Attribute("attribute")?.Value).FirstOrDefault();
            if (activeOrder != managedOrder)
            {
                diffs.Add($"Sort: {managedOrder ?? "none"} → {activeOrder ?? "none"}");
            }
        }
        catch
        {
            // XML parsing failed
        }

        return diffs;
    }

    private static List<string> CompareLayoutXml(string activeLayout, string managedLayout)
    {
        var diffs = new List<string>();

        try
        {
            var activeDoc = XDocument.Parse(activeLayout);
            var managedDoc = XDocument.Parse(managedLayout);

            var activeCells = activeDoc.Descendants("cell").Select(c => c.Attribute("name")?.Value).Where(n => n != null).ToList();
            var managedCells = managedDoc.Descendants("cell").Select(c => c.Attribute("name")?.Value).Where(n => n != null).ToList();

            var added = activeCells.Except(managedCells).ToList();
            var removed = managedCells.Except(activeCells).ToList();

            if (added.Count > 0)
                diffs.Add($"+Visible: {string.Join(", ", added.Take(4))}{(added.Count > 4 ? $" (+{added.Count - 4})" : "")}");
            if (removed.Count > 0)
                diffs.Add($"-Visible: {string.Join(", ", removed.Take(4))}{(removed.Count > 4 ? $" (+{removed.Count - 4})" : "")}");

            // Check column width changes
            var activeWidths = activeDoc.Descendants("cell")
                .Select(c => new { Name = c.Attribute("name")?.Value, Width = c.Attribute("width")?.Value })
                .Where(c => c.Name != null)
                .ToDictionary(c => c.Name!, c => c.Width);
            var managedWidths = managedDoc.Descendants("cell")
                .Select(c => new { Name = c.Attribute("name")?.Value, Width = c.Attribute("width")?.Value })
                .Where(c => c.Name != null)
                .ToDictionary(c => c.Name!, c => c.Width);

            var widthChanges = activeWidths.Keys.Intersect(managedWidths.Keys)
                .Where(k => activeWidths[k] != managedWidths[k])
                .ToList();
            if (widthChanges.Count > 0)
            {
                diffs.Add($"Width changes: {widthChanges.Count} columns");
            }
        }
        catch
        {
            // XML parsing failed
        }

        return diffs;
    }

    private static List<string> CompareChartLayers(JsonElement active, JsonElement managed)
    {
        var diffs = new List<string>();

        try
        {
            // Compare data description
            if (TryGetString(active, "datadescription", out var activeData) &&
                TryGetString(managed, "datadescription", out var managedData))
            {
                if (activeData != managedData)
                {
                    diffs.Add("Data definition changed");
                }
            }

            // Compare presentation
            if (TryGetString(active, "presentationdescription", out var activePres) &&
                TryGetString(managed, "presentationdescription", out var managedPres))
            {
                if (activePres != managedPres)
                {
                    diffs.Add("Chart presentation changed");
                }
            }

            CompareJsonProperty(active, managed, "ChartType", diffs);
            CompareJsonProperty(active, managed, "Name", diffs);
        }
        catch
        {
            diffs.Add("Could not fully parse chart structure");
        }

        return diffs;
    }

    private static List<string> CompareGenericJson(JsonElement active, JsonElement managed)
    {
        var diffs = new List<string>();

        try
        {
            // Count top-level property differences
            var activeProps = GetPropertyNames(active);
            var managedProps = GetPropertyNames(managed);

            var added = activeProps.Except(managedProps).ToList();
            var removed = managedProps.Except(activeProps).ToList();

            if (added.Count > 0)
                diffs.Add($"+Properties: {string.Join(", ", added.Take(5))}{(added.Count > 5 ? $" (+{added.Count - 5})" : "")}");
            if (removed.Count > 0)
                diffs.Add($"-Properties: {string.Join(", ", removed.Take(5))}{(removed.Count > 5 ? $" (+{removed.Count - 5})" : "")}");

            // Check for value changes in common properties
            var common = activeProps.Intersect(managedProps);
            int changedCount = 0;
            foreach (var prop in common)
            {
                if (active.TryGetProperty(prop, out var activeVal) && managed.TryGetProperty(prop, out var managedVal))
                {
                    if (activeVal.ToString() != managedVal.ToString())
                    {
                        changedCount++;
                    }
                }
            }
            if (changedCount > 0)
            {
                diffs.Add($"Modified properties: {changedCount}");
            }
        }
        catch
        {
            diffs.Add("Could not parse JSON structure");
        }

        return diffs;
    }

    private static List<string> CompareXmlContent(string? activeContent, string? managedContent, int componentType)
    {
        var diffs = new List<string>();

        if (string.IsNullOrEmpty(activeContent) || string.IsNullOrEmpty(managedContent))
        {
            return diffs;
        }

        try
        {
            var activeDoc = XDocument.Parse(activeContent);
            var managedDoc = XDocument.Parse(managedContent);

            // Generic XML comparison based on component type
            switch (componentType)
            {
                case 60: // Form
                    diffs.AddRange(CompareFormXml(activeContent, managedContent));
                    break;
                case 26: // View
                    diffs.AddRange(CompareFetchXml(activeContent, managedContent));
                    break;
                default:
                    // Generic element count comparison
                    var activeElements = activeDoc.Descendants().Count();
                    var managedElements = managedDoc.Descendants().Count();
                    if (activeElements != managedElements)
                    {
                        diffs.Add($"Elements: {managedElements} → {activeElements}");
                    }
                    break;
            }
        }
        catch
        {
            diffs.Add("Content changed (not parseable as XML)");
        }

        return diffs;
    }

    private static async Task<string> GetComponentSpecificDiffAsync(int componentType, Guid componentId, string entityName, string managedSolutionName)
    {
        // Fallback for when layer JSON isn't available - describe current component state
        var diffs = new List<string>();
        diffs.Add($"[vs {managedSolutionName}]");

        try
        {
            switch (componentType)
            {
                case 2: // Attribute
                    if (!string.IsNullOrEmpty(entityName))
                    {
                        var attrDiff = await GetAttributeLayerDiffAsync(componentId, entityName);
                        diffs.Add(attrDiff);
                    }
                    else
                    {
                        diffs.Add("Attribute customized");
                    }
                    break;

                case 60: // Form
                    var formDiff = await GetFormLayerDiffAsync(componentId);
                    diffs.Add(formDiff);
                    break;

                case 26: // View
                    var viewDiff = await GetViewLayerDiffAsync(componentId);
                    diffs.Add(viewDiff);
                    break;

                case 59: // Chart
                    var chartDiff = await GetChartLayerDiffAsync(componentId);
                    diffs.Add(chartDiff);
                    break;

                case 61: // Web Resource
                    var wrDiff = await GetWebResourceLayerDiffAsync(componentId);
                    diffs.Add(wrDiff);
                    break;

                case 29: // Workflow
                    var wfDiff = await GetWorkflowLayerDiffAsync(componentId);
                    diffs.Add(wfDiff);
                    break;

                default:
                    diffs.Add($"Has unmanaged customizations ({GetComponentTypeName(componentType)})");
                    break;
            }
        }
        catch (Exception ex)
        {
            diffs.Add($"Error: {ex.Message}");
        }

        return string.Join("; ", diffs);
    }

    private static async Task<string> GetAttributeLayerDiffAsync(Guid attributeMetadataId, string entityName)
    {
        try
        {
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
                return "Attribute not found";
            }

            // For attributes, we can note what's customizable
            var info = new List<string>();
            if (!attribute.IsManaged.GetValueOrDefault())
            {
                info.Add("Unmanaged attribute");
            }
            if (attribute.IsCustomizable?.Value == true)
            {
                info.Add("Customizable");
            }
            info.Add($"Type: {attribute.AttributeType}");

            return string.Join(", ", info);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static async Task<string> GetFormLayerDiffAsync(Guid formId)
    {
        try
        {
            var form = await Task.Run(() => _serviceClient!.Retrieve("systemform", formId,
                new ColumnSet("name", "formxml", "ismanaged")));

            var formXml = form.GetAttributeValue<string>("formxml");
            var isManaged = form.GetAttributeValue<bool>("ismanaged");

            if (string.IsNullOrEmpty(formXml))
            {
                return "Form XML not available";
            }

            var doc = XDocument.Parse(formXml);
            var tabs = doc.Descendants("tab").Count();
            var sections = doc.Descendants("section").Count();
            var controls = doc.Descendants("control").Count();

            return $"Current: {tabs} tabs, {sections} sections, {controls} controls";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static async Task<string> GetViewLayerDiffAsync(Guid viewId)
    {
        try
        {
            var view = await Task.Run(() => _serviceClient!.Retrieve("savedquery", viewId,
                new ColumnSet("name", "fetchxml", "layoutxml", "ismanaged")));

            var fetchXml = view.GetAttributeValue<string>("fetchxml");
            var layoutXml = view.GetAttributeValue<string>("layoutxml");

            var info = new List<string>();

            if (!string.IsNullOrEmpty(fetchXml))
            {
                var fetchDoc = XDocument.Parse(fetchXml);
                var attrs = fetchDoc.Descendants("attribute").Count();
                var conditions = fetchDoc.Descendants("condition").Count();
                info.Add($"Query: {attrs} columns, {conditions} filters");
            }

            if (!string.IsNullOrEmpty(layoutXml))
            {
                var layoutDoc = XDocument.Parse(layoutXml);
                var cells = layoutDoc.Descendants("cell").Count();
                info.Add($"Layout: {cells} visible columns");
            }

            return info.Count > 0 ? string.Join("; ", info) : "View details not available";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static async Task<string> GetChartLayerDiffAsync(Guid chartId)
    {
        try
        {
            var chart = await Task.Run(() => _serviceClient!.Retrieve("savedqueryvisualization", chartId,
                new ColumnSet("name", "datadescription", "presentationdescription")));

            var name = chart.GetAttributeValue<string>("name") ?? "Unknown";
            var dataDesc = chart.GetAttributeValue<string>("datadescription");

            var info = new List<string> { $"Chart: {name}" };

            if (!string.IsNullOrEmpty(dataDesc))
            {
                try
                {
                    var doc = XDocument.Parse(dataDesc);
                    var measures = doc.Descendants("measure").Count();
                    info.Add($"{measures} measures");
                }
                catch { }
            }

            return string.Join(", ", info);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static async Task<string> GetWebResourceLayerDiffAsync(Guid webResourceId)
    {
        try
        {
            var wr = await Task.Run(() => _serviceClient!.Retrieve("webresource", webResourceId,
                new ColumnSet("name", "webresourcetype", "displayname")));

            var name = wr.GetAttributeValue<string>("name") ?? "Unknown";
            var wrType = wr.GetAttributeValue<OptionSetValue>("webresourcetype")?.Value ?? 0;

            var typeName = wrType switch
            {
                1 => "HTML", 2 => "CSS", 3 => "JavaScript", 4 => "XML",
                5 => "PNG", 6 => "JPG", 7 => "GIF", 10 => "ICO", 11 => "SVG",
                _ => $"Type {wrType}"
            };

            return $"Web Resource ({typeName}): {name}";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static async Task<string> GetWorkflowLayerDiffAsync(Guid workflowId)
    {
        try
        {
            var wf = await Task.Run(() => _serviceClient!.Retrieve("workflow", workflowId,
                new ColumnSet("name", "category", "primaryentity", "mode")));

            var name = wf.GetAttributeValue<string>("name") ?? "Unknown";
            var category = wf.GetAttributeValue<OptionSetValue>("category")?.Value ?? 0;
            var entity = wf.GetAttributeValue<string>("primaryentity") ?? "none";
            var mode = wf.GetAttributeValue<OptionSetValue>("mode")?.Value ?? 0;

            var categoryName = category switch
            {
                0 => "Workflow", 1 => "Dialog", 2 => "Business Rule",
                3 => "Action", 4 => "BPF", 5 => "Flow",
                _ => $"Category {category}"
            };

            var modeName = mode == 1 ? "Real-time" : "Background";

            return $"{categoryName} ({modeName}): {name} on {entity}";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    // Helper methods for JSON comparison
    private static int GetJsonArrayCount(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Array)
        {
            return prop.GetArrayLength();
        }
        return 0;
    }

    private static int CountNestedElements(JsonElement element, string propertyName)
    {
        int count = 0;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Name == propertyName && prop.Value.ValueKind == JsonValueKind.Array)
                {
                    count += prop.Value.GetArrayLength();
                }
                count += CountNestedElements(prop.Value, propertyName);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                count += CountNestedElements(item, propertyName);
            }
        }
        return count;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            value = prop.GetString() ?? string.Empty;
            return true;
        }
        return false;
    }

    private static void CompareJsonProperty(JsonElement active, JsonElement managed, string propertyName, List<string> diffs)
    {
        var activeHas = active.TryGetProperty(propertyName, out var activeVal);
        var managedHas = managed.TryGetProperty(propertyName, out var managedVal);

        if (activeHas && managedHas)
        {
            var activeStr = activeVal.ToString();
            var managedStr = managedVal.ToString();
            if (activeStr != managedStr)
            {
                diffs.Add($"{propertyName}: {managedStr} → {activeStr}");
            }
        }
        else if (activeHas && !managedHas)
        {
            diffs.Add($"+{propertyName}: {activeVal}");
        }
        else if (!activeHas && managedHas)
        {
            diffs.Add($"-{propertyName}: {managedVal}");
        }
    }

    private static HashSet<string> GetPropertyNames(JsonElement element)
    {
        var names = new HashSet<string>();
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                names.Add(prop.Name);
            }
        }
        return names;
    }

    private static async Task<string> GetPowerPagesDiffAsync(Entity ppComponent)
    {
        try
        {
            var componentName = ppComponent.GetAttributeValue<string>("name") ?? "Unknown";
            var componentType = ppComponent.GetAttributeValue<OptionSetValue>("powerpagecomponenttype")?.Value ?? 0;
            var componentTypeName = GetPowerPagesComponentTypeName(componentType);

            var diffItems = new List<string>
            {
                $"Type: {componentTypeName}",
                "Has unmanaged customizations in Active Solution"
            };

            var content = ppComponent.GetAttributeValue<string>("content");
            if (!string.IsNullOrEmpty(content))
            {
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
