using Microsoft.Crm.Sdk.Messages;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System.ServiceModel;

namespace DataverseUnmanagedLayerFixer;

class Program
{
    private static ServiceClient? _serviceClient;

    static async Task Main(string[] args)
    {
        Console.WriteLine("===========================================");
        Console.WriteLine("  Dataverse Unmanaged Layer Fixer Tool");
        Console.WriteLine("===========================================");
        Console.WriteLine();

        string environmentUrl = GetEnvironmentUrl(args);

        if (!ConnectToDataverse(environmentUrl))
        {
            Console.WriteLine("Failed to connect to Dataverse. Exiting.");
            return;
        }

        Console.WriteLine();
        Console.WriteLine("Connected successfully!");
        Console.WriteLine();

        var solution = await SelectSolutionAsync();
        if (solution == null)
        {
            Console.WriteLine("No solution selected. Exiting.");
            return;
        }

        await ProcessSolutionComponentsAsync(solution);

        Console.WriteLine();
        Console.WriteLine("Processing complete. Press any key to exit.");
        Console.ReadKey();
    }

    private static string GetEnvironmentUrl(string[] args)
    {
        if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
        {
            return args[0];
        }

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

    private static async Task<Entity?> SelectSolutionAsync()
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

        // Build a set of component IDs for fast lookup
        var componentIds = new HashSet<string>(
            allComponents
                .Select(c => c.GetAttributeValue<Guid>("objectid"))
                .Where(id => id != Guid.Empty)
                .Select(id => id.ToString().ToLowerInvariant())
        );

        // Build a lookup of component types by object ID
        var componentTypeMap = allComponents
            .Where(c => c.GetAttributeValue<Guid>("objectid") != Guid.Empty)
            .ToDictionary(
                c => c.GetAttributeValue<Guid>("objectid").ToString().ToLowerInvariant(),
                c => c.GetAttributeValue<OptionSetValue>("componenttype")?.Value ?? 0
            );

        Console.WriteLine("Fetching all Active (unmanaged) layers...");

        // Fetch ALL Active layers at once (much faster than per-component queries)
        var allActiveLayers = await RetrieveAllActiveLayersAsync();

        Console.WriteLine($"Found {allActiveLayers.Count} total Active layers in the environment.");

        // Debug: Show sample component IDs from both sources to help identify format mismatches
        if (allActiveLayers.Count > 0 && componentIds.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("[DEBUG] Sample solution component IDs (first 3):");
            foreach (var id in componentIds.Take(3))
            {
                Console.WriteLine($"  - {id}");
            }

            Console.WriteLine("[DEBUG] Sample Active layer component IDs (first 5):");
            foreach (var layer in allActiveLayers.Take(5))
            {
                var layerComponentId = layer.GetAttributeValue<string>("msdyn_componentid") ?? "null";
                var layerName = layer.GetAttributeValue<string>("msdyn_name") ?? "Unknown";
                Console.WriteLine($"  - {layerComponentId} ({layerName})");
            }
            Console.WriteLine();
        }

        // Filter to only layers that match our solution components
        // Try matching with and without braces, and normalized to lowercase
        var matchingLayers = allActiveLayers
            .Where(layer =>
            {
                var componentId = layer.GetAttributeValue<string>("msdyn_componentid");
                if (componentId == null) return false;

                // Normalize: remove braces if present and convert to lowercase
                var normalizedId = componentId.Trim().ToLowerInvariant();
                if (normalizedId.StartsWith("{") && normalizedId.EndsWith("}"))
                {
                    normalizedId = normalizedId.Substring(1, normalizedId.Length - 2);
                }

                return componentIds.Contains(normalizedId);
            })
            .ToList();

        Console.WriteLine($"Found {matchingLayers.Count} unmanaged layers for components in this solution.");
        Console.WriteLine();

        if (matchingLayers.Count == 0)
        {
            Console.WriteLine("No unmanaged layers found for this solution's components.");
            Console.WriteLine();
            Console.WriteLine("[DEBUG] This could mean:");
            Console.WriteLine("  - The solution components have no unmanaged customizations");
            Console.WriteLine("  - The component ID formats don't match between tables");
            Console.WriteLine("  - Power Pages or other components use a different ID scheme");
            return;
        }

        int layersRemoved = 0;
        bool removeAll = false;

        for (int i = 0; i < matchingLayers.Count; i++)
        {
            var layer = matchingLayers[i];
            var componentId = layer.GetAttributeValue<string>("msdyn_componentid")?.ToLowerInvariant() ?? "";
            var componentType = componentTypeMap.GetValueOrDefault(componentId, 0);

            DisplayLayerInfo(layer, componentType);

            if (removeAll)
            {
                Console.WriteLine("Auto-removing unmanaged layer...");
                if (await RemoveUnmanagedLayerAsync(layer, componentType))
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
                if (await RemoveUnmanagedLayerAsync(layer, componentType))
                    layersRemoved++;
                Console.WriteLine();
                continue;
            }

            if (response == "y")
            {
                if (await RemoveUnmanagedLayerAsync(layer, componentType))
                    layersRemoved++;
            }
            else
            {
                Console.WriteLine("Skipped.");
            }

            Console.WriteLine();
        }

        Console.WriteLine("-------------------------------------------");
        Console.WriteLine($"Summary:");
        Console.WriteLine($"  Components with unmanaged layers: {matchingLayers.Count}");
        Console.WriteLine($"  Unmanaged layers removed: {layersRemoved}");
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

        while (true)
        {
            var results = await Task.Run(() => _serviceClient!.RetrieveMultiple(componentQuery));
            allComponents.AddRange(results.Entities);

            if (results.MoreRecords)
            {
                componentQuery.PageInfo.PageNumber++;
                componentQuery.PageInfo.PagingCookie = results.PagingCookie;
            }
            else
            {
                break;
            }
        }

        return allComponents;
    }

    private static async Task<List<Entity>> RetrieveAllActiveLayersAsync()
    {
        var allLayers = new List<Entity>();

        var layerQuery = new QueryExpression("msdyn_componentlayer")
        {
            ColumnSet = new ColumnSet(true),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression("msdyn_solutionname", ConditionOperator.Equal, "Active")
                }
            },
            PageInfo = new PagingInfo
            {
                Count = 5000,
                PageNumber = 1,
                ReturnTotalRecordCount = false
            }
        };

        while (true)
        {
            var results = await Task.Run(() => _serviceClient!.RetrieveMultiple(layerQuery));
            allLayers.AddRange(results.Entities);

            if (results.MoreRecords)
            {
                layerQuery.PageInfo.PageNumber++;
                layerQuery.PageInfo.PagingCookie = results.PagingCookie;
            }
            else
            {
                break;
            }
        }

        return allLayers;
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

    private static async Task<bool> RemoveUnmanagedLayerAsync(Entity layer, int componentType)
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
                    { "SolutionComponentName", GetSolutionComponentLogicalName(componentType) },
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

    private static string GetSolutionComponentLogicalName(int componentType)
    {
        return componentType switch
        {
            1 => "entity",
            2 => "attribute",
            3 => "relationship",
            9 => "optionset",
            10 => "entityrelationship",
            20 => "role",
            24 => "systemform",
            26 => "savedquery",
            29 => "workflow",
            31 => "report",
            36 => "emailtemplate",
            59 => "savedqueryvisualization",
            60 => "systemform",
            61 => "webresource",
            62 => "sitemap",
            63 => "connectionrole",
            66 => "customcontrol",
            80 => "appmodule",
            90 => "plugintype",
            91 => "pluginassembly",
            92 => "sdkmessageprocessingstep",
            300 => "canvasapp",
            380 => "environmentvariabledefinition",
            381 => "environmentvariablevalue",
            _ => "entity"
        };
    }
}
