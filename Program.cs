using Microsoft.Crm.Sdk.Messages;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using System.Net.Http.Headers;
using System.ServiceModel;
using System.Text.Json;

namespace DataverseUnmanagedLayerFixer;

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

        // Main loop - allow processing multiple solutions
        bool continueProcessing = true;
        while (continueProcessing)
        {
            var solution = await SelectSolutionAsync(solutionName);
            if (solution == null)
            {
                Console.WriteLine("No solution selected. Exiting.");
                break;
            }

            // Clear the solution name after first use (so user can select interactively next time)
            solutionName = null;

            await ProcessSolutionComponentsAsync(solution);

            Console.WriteLine();
            Console.Write("Do you want to check another solution? (y/n): ");
            string? response = Console.ReadLine()?.Trim().ToLower();
            continueProcessing = response == "y" || response == "yes";
            Console.WriteLine();
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

    private static async Task<Entity?> SelectSolutionAsync(string? solutionNameFilter = null)
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
            return null;
        }

        // If solution name was provided via command line, find it automatically
        if (!string.IsNullOrWhiteSpace(solutionNameFilter))
        {
            var matchingSolution = solutions.Entities.FirstOrDefault(s =>
            {
                var friendlyName = s.GetAttributeValue<string>("friendlyname") ?? "";
                var uniqueName = s.GetAttributeValue<string>("uniquename") ?? "";
                return friendlyName.Equals(solutionNameFilter, StringComparison.OrdinalIgnoreCase) ||
                       uniqueName.Equals(solutionNameFilter, StringComparison.OrdinalIgnoreCase);
            });

            if (matchingSolution != null)
            {
                var name = matchingSolution.GetAttributeValue<string>("friendlyname") ?? "Unknown";
                Console.WriteLine($"Auto-selected solution: {name}");
                return matchingSolution;
            }
            else
            {
                Console.WriteLine($"Solution '{solutionNameFilter}' not found. Showing list...");
            }
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
        Console.Write("Enter the number of the solution to check (or 0 to exit): ");

        while (true)
        {
            string? input = Console.ReadLine();
            if (int.TryParse(input, out int selection))
            {
                if (selection == 0)
                    return null;

                if (selection >= 1 && selection <= solutions.Entities.Count)
                    return solutions.Entities[selection - 1];
            }

            Console.Write("Invalid selection. Please enter a valid number: ");
        }
    }

    private static async Task ProcessSolutionComponentsAsync(Entity solution)
    {
        Guid solutionId = solution.GetAttributeValue<Guid>("solutionid");
        string solutionName = solution.GetAttributeValue<string>("friendlyname") ?? "Unknown";

        Console.WriteLine();
        Console.WriteLine($"Processing solution: {solutionName}");
        Console.WriteLine("Fetching solution components...");

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
            return;
        }

        int layersRemoved = 0;
        bool removeAll = false;

        // Process standard component layers
        for (int i = 0; i < matchingLayers.Count; i++)
        {
            var (component, layer, logicalName) = matchingLayers[i];
            var componentType = component.GetAttributeValue<OptionSetValue>("componenttype")?.Value ?? 0;

            DisplayLayerInfo(layer, componentType);

            if (removeAll)
            {
                Console.WriteLine("Auto-removing unmanaged layer...");
                if (await RemoveUnmanagedLayerAsync(layer, logicalName))
                    layersRemoved++;
                Console.WriteLine();
                continue;
            }

            Console.Write("Do you want to remove this unmanaged layer? (y/n/a=all/s=skip all): ");
            string? response = Console.ReadLine()?.Trim().ToLower();

            if (response == "s")
            {
                Console.WriteLine("Skipping remaining layers.");
                break;
            }

            if (response == "a")
            {
                removeAll = true;
                if (await RemoveUnmanagedLayerAsync(layer, logicalName))
                    layersRemoved++;
                Console.WriteLine();
                continue;
            }

            if (response == "y")
            {
                if (await RemoveUnmanagedLayerAsync(layer, logicalName))
                    layersRemoved++;
            }
            else
            {
                Console.WriteLine("Skipped.");
            }

            Console.WriteLine();
        }

        // Process Power Pages components
        int powerPagesRemoved = 0;
        if (unmanagedPowerPagesComponents.Count > 0 && !removeAll)
        {
            Console.WriteLine();
            Console.WriteLine("===========================================");
            Console.WriteLine("  POWER PAGES UNMANAGED CUSTOMIZATIONS");
            Console.WriteLine("===========================================");
        }

        for (int i = 0; i < unmanagedPowerPagesComponents.Count; i++)
        {
            var ppComponent = unmanagedPowerPagesComponents[i];
            DisplayPowerPagesComponentInfo(ppComponent);

            if (removeAll)
            {
                Console.WriteLine("Auto-removing Power Pages unmanaged customization...");
                if (await RemovePowerPagesUnmanagedCustomizationAsync(ppComponent))
                    powerPagesRemoved++;
                Console.WriteLine();
                continue;
            }

            Console.Write("Do you want to remove this unmanaged customization? (y/n/a=all/s=skip all): ");
            string? response = Console.ReadLine()?.Trim().ToLower();

            if (response == "s")
            {
                Console.WriteLine("Skipping remaining components.");
                break;
            }

            if (response == "a")
            {
                removeAll = true;
                if (await RemovePowerPagesUnmanagedCustomizationAsync(ppComponent))
                    powerPagesRemoved++;
                Console.WriteLine();
                continue;
            }

            if (response == "y")
            {
                if (await RemovePowerPagesUnmanagedCustomizationAsync(ppComponent))
                    powerPagesRemoved++;
            }
            else
            {
                Console.WriteLine("Skipped.");
            }

            Console.WriteLine();
        }

        Console.WriteLine("-------------------------------------------");
        Console.WriteLine($"Summary:");
        Console.WriteLine($"  Standard components with unmanaged layers: {matchingLayers.Count}");
        Console.WriteLine($"  Power Pages components with unmanaged customizations: {unmanagedPowerPagesComponents.Count}");
        Console.WriteLine($"  Total unmanaged layers removed: {layersRemoved + powerPagesRemoved}");
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
        foreach (var (id, name) in forms)
        {
            var comp = new Entity("solutioncomponent");
            comp["objectid"] = id;
            comp["componenttype"] = new OptionSetValue(60);
            results.Add((comp, 60, id, "systemform"));
        }

        // Query views (type 26) in Active Solution that belong to our entities
        Console.Write("\r  Checking views...                                        ");
        var views = await GetEntitySubcomponentsAsync<Guid>(
            "savedquery", "savedqueryid", "returnedtypecode", "name",
            entityNames, activeComponents, 26);
        foreach (var (id, name) in views)
        {
            var comp = new Entity("solutioncomponent");
            comp["objectid"] = id;
            comp["componenttype"] = new OptionSetValue(26);
            results.Add((comp, 26, id, "savedquery"));
        }

        // Query charts (type 59) in Active Solution that belong to our entities
        Console.Write("\r  Checking charts...                                       ");
        var charts = await GetEntitySubcomponentsAsync<Guid>(
            "savedqueryvisualization", "savedqueryvisualizationid", "primaryentitytypecode", "name",
            entityNames, activeComponents, 59);
        foreach (var (id, name) in charts)
        {
            var comp = new Entity("solutioncomponent");
            comp["objectid"] = id;
            comp["componenttype"] = new OptionSetValue(59);
            results.Add((comp, 59, id, "savedqueryvisualization"));
        }

        Console.WriteLine($"\r  Found {results.Count} entity subcomponents with customizations.          ");
        return results;
    }

    private static async Task<List<(Guid Id, string Name)>> GetEntitySubcomponentsAsync<T>(
        string tableName, string idColumn, string entityColumn, string nameColumn,
        HashSet<string> entityNames, HashSet<(Guid ObjectId, int ComponentType)> activeComponents, int componentType)
    {
        var results = new List<(Guid Id, string Name)>();

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

                    // Check if this component is in the Active Solution
                    if (activeComponents.Contains((id, componentType)))
                    {
                        results.Add((id, name));
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

                    case 2: // Attribute
                        await FetchComponentNamesAsync("attribute", "attributeid", "logicalname", objectIds, names, "Attribute");
                        break;

                    case 20: // Role
                        await FetchComponentNamesAsync("role", "roleid", "name", objectIds, names, "Role");
                        break;

                    case 26: // Saved Query (View)
                        await FetchComponentNamesAsync("savedquery", "savedqueryid", "name", objectIds, names, "View");
                        break;

                    case 29: // Workflow
                        await FetchComponentNamesAsync("workflow", "workflowid", "name", objectIds, names, "Workflow");
                        break;

                    case 60: // System Form
                        await FetchComponentNamesAsync("systemform", "formid", "name", objectIds, names, "Form");
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
}
