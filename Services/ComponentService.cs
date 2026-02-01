using DataverseUnmanagedLayerFixer.Helpers;
using DataverseUnmanagedLayerFixer.Models;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using System.ServiceModel;
using System.Threading;

namespace DataverseUnmanagedLayerFixer.Services;

/// <summary>
/// Holds mutable state for processing (workaround for async methods not supporting ref parameters).
/// </summary>
public class ProcessingState
{
    public bool RemoveAll { get; set; }
    public bool SkipAll { get; set; }
}

/// <summary>
/// Service for processing solution components and detecting unmanaged layers.
/// </summary>
public class ComponentService
{
    private readonly DataverseService _dataverseService;

    public ComponentService(DataverseService dataverseService)
    {
        _dataverseService = dataverseService;
    }

    /// <summary>
    /// Processes all components in a solution and returns results.
    /// </summary>
    public async Task<List<ComponentResult>> ProcessSolutionAsync(Entity solution, bool exportOnly)
    {
        var solutionId = solution.GetAttributeValue<Guid>("solutionid");
        var solutionName = solution.GetAttributeValue<string>("friendlyname") ?? "Unknown";

        Console.WriteLine();
        Console.WriteLine($"Processing solution: {solutionName}");
        Console.WriteLine("Fetching solution components...");

        var componentResults = new List<ComponentResult>();
        var allComponents = await RetrieveAllComponentsAsync(solutionId);

        Console.WriteLine($"Found {allComponents.Count} components in the solution.");
        PrintComponentTypeBreakdown(allComponents);

        // Separate Power Pages from standard components
        var (standardComponents, powerPagesComponents) = SeparateComponentTypes(allComponents);
        Console.WriteLine($"  Standard components: {standardComponents.Count}");
        Console.WriteLine($"  Power Pages components: {powerPagesComponents.Count}");
        Console.WriteLine();

        // Build entity metadata map
        var entityMetadataMap = await GetEntityMetadataMapAsync(standardComponents);

        // Get unmanaged layers for standard components
        var matchingLayers = await GetUnmanagedLayersAsync(standardComponents, entityMetadataMap);
        Console.WriteLine($"Found {matchingLayers.Count} standard components with unmanaged layers.");

        // Get unmanaged Power Pages components
        var unmanagedPowerPages = await GetUnmanagedPowerPagesComponentsAsync(powerPagesComponents);
        Console.WriteLine($"Found {unmanagedPowerPages.Count} Power Pages components with unmanaged customizations.");

        var totalUnmanaged = matchingLayers.Count + unmanagedPowerPages.Count;
        Console.WriteLine();

        if (totalUnmanaged == 0)
        {
            Console.WriteLine("No unmanaged layers found for this solution's components.");
            return componentResults;
        }

        // Process components
        int layersRemoved = 0;
        var state = new ProcessingState { RemoveAll = false, SkipAll = exportOnly };

        // Process standard components
        layersRemoved += await ProcessStandardComponentsAsync(
            matchingLayers, componentResults, exportOnly, state);

        // Process Power Pages components
        int powerPagesRemoved = await ProcessPowerPagesComponentsAsync(
            unmanagedPowerPages, componentResults, exportOnly, state);

        // Print summary
        PrintSummary(matchingLayers.Count, unmanagedPowerPages.Count, layersRemoved + powerPagesRemoved);

        return componentResults;
    }

    private async Task<List<Entity>> RetrieveAllComponentsAsync(Guid solutionId)
    {
        var query = new QueryExpression("solutioncomponent")
        {
            ColumnSet = new ColumnSet("componenttype", "objectid", "solutioncomponentid"),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression("solutionid", ConditionOperator.Equal, solutionId)
                }
            },
            PageInfo = new PagingInfo { Count = 5000, PageNumber = 1 }
        };

        return await _dataverseService.RetrieveAllAsync(query, (page, count) =>
        {
            Console.Write($"\r  Fetching components... (page {page}, {count} found)    ");
        });
    }

    private void PrintComponentTypeBreakdown(List<Entity> components)
    {
        var counts = components
            .GroupBy(c => c.GetAttributeValue<OptionSetValue>("componenttype")?.Value ?? 0)
            .OrderByDescending(g => g.Count())
            .Take(10);

        Console.WriteLine("[DEBUG] Top component types in solution:");
        foreach (var group in counts)
        {
            Console.WriteLine($"  - Type {group.Key} ({ComponentTypeHelper.GetComponentTypeName(group.Key)}): {group.Count()}");
        }
        Console.WriteLine();
    }

    private (List<Entity> Standard, List<Entity> PowerPages) SeparateComponentTypes(List<Entity> components)
    {
        var standard = components
            .Where(c => !ComponentTypeHelper.PowerPagesComponentTypes.Contains(
                c.GetAttributeValue<OptionSetValue>("componenttype")?.Value ?? 0))
            .ToList();

        var powerPages = components
            .Where(c => ComponentTypeHelper.PowerPagesComponentTypes.Contains(
                c.GetAttributeValue<OptionSetValue>("componenttype")?.Value ?? 0))
            .ToList();

        return (standard, powerPages);
    }

    private async Task<Dictionary<Guid, string>> GetEntityMetadataMapAsync(List<Entity> components)
    {
        var entityComponents = components
            .Where(c => c.GetAttributeValue<OptionSetValue>("componenttype")?.Value == 1)
            .ToList();

        if (entityComponents.Count == 0)
            return new Dictionary<Guid, string>();

        Console.WriteLine($"Resolving entity names for {entityComponents.Count} entity components...");

        var metadataIds = entityComponents
            .Select(c => c.GetAttributeValue<Guid>("objectid"))
            .Where(id => id != Guid.Empty)
            .ToHashSet();

        var entityMap = new Dictionary<Guid, string>();

        try
        {
            var request = new RetrieveAllEntitiesRequest
            {
                EntityFilters = EntityFilters.Entity,
                RetrieveAsIfPublished = false
            };

            var response = (RetrieveAllEntitiesResponse)await _dataverseService.ExecuteAsync(request);

            foreach (var metadata in response.EntityMetadata)
            {
                if (metadataIds.Contains(metadata.MetadataId ?? Guid.Empty))
                {
                    entityMap[metadata.MetadataId!.Value] = metadata.LogicalName;
                }
            }

            Console.WriteLine($"  Resolved {entityMap.Count} entity names.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Warning: Could not retrieve entity metadata: {ex.Message}");
        }

        return entityMap;
    }

    private async Task<List<(Entity Component, Entity Layer, string LogicalName)>> GetUnmanagedLayersAsync(
        List<Entity> components, Dictionary<Guid, string> entityMetadataMap)
    {
        var results = new List<(Entity Component, Entity Layer, string LogicalName)>();

        // Step 1: Build a list of components to check (excluding entities - we check their subcomponents)
        var componentsToCheck = new List<(Entity Component, int ComponentType, Guid ObjectId, string SolutionComponentName)>();
        foreach (var component in components)
        {
            var componentType = component.GetAttributeValue<OptionSetValue>("componenttype")?.Value ?? 0;
            var objectId = component.GetAttributeValue<Guid>("objectid");
            if (objectId != Guid.Empty && componentType != 1) // Skip entities - check their subcomponents
            {
                var solutionComponentName = ComponentTypeHelper.GetSolutionComponentName(componentType);
                if (!string.IsNullOrEmpty(solutionComponentName))
                {
                    componentsToCheck.Add((component, componentType, objectId, solutionComponentName));
                }
            }
        }

        Console.WriteLine($"  Checking {componentsToCheck.Count} components for Active layers...");

        // Step 2: Check each component for Active layers by querying msdyn_componentlayer
        // Group by solution component name for batched queries
        var byType = componentsToCheck.GroupBy(c => c.SolutionComponentName);
        var explicitMatches = new List<(Entity Component, Entity Layer, int ComponentType, Guid ObjectId, string LogicalName)>();

        foreach (var group in byType)
        {
            var solutionComponentName = group.Key;
            var items = group.ToList();
            var objectIds = items.Select(i => i.ObjectId).ToList();

            Console.Write($"\r  Checking {solutionComponentName} ({items.Count})...                    ");

            // Query layers for these components
            var activeLayers = await GetActiveLayersForComponentsAsync(objectIds, solutionComponentName);

            foreach (var item in items)
            {
                if (activeLayers.TryGetValue(item.ObjectId, out var layer))
                {
                    var logicalName = ComponentTypeHelper.GetSolutionComponentLogicalName(item.ComponentType) ?? "unknown";
                    explicitMatches.Add((item.Component, layer, item.ComponentType, item.ObjectId, logicalName));
                }
            }
        }

        // Step 3: Fetch proper display names for explicit components
        if (explicitMatches.Count > 0)
        {
            Console.Write("\r  Fetching component display names...                     ");
            await EnrichComponentNamesAsync(explicitMatches);
        }

        foreach (var (component, layer, componentType, objectId, logicalName) in explicitMatches)
        {
            results.Add((component, layer, logicalName));
        }

        // Step 4: Get managed entity names for subcomponent lookup
        var managedEntityNames = GetManagedEntityNames(components, entityMetadataMap);
        Console.WriteLine($"\r  Found {managedEntityNames.Count} entities in managed solution.              ");

        // Step 5: Find entity subcomponents with Active layers
        if (managedEntityNames.Count > 0)
        {
            var subcomponentResults = await GetEntitySubcomponentsWithActiveLayersAsync(managedEntityNames);
            results.AddRange(subcomponentResults);
        }

        Console.WriteLine($"  Found {results.Count} components with unmanaged layers.");

        return results;
    }

    private async Task<Dictionary<Guid, Entity>> GetActiveLayersForComponentsAsync(
        List<Guid> objectIds, string solutionComponentName)
    {
        var activeLayers = new Dictionary<Guid, Entity>();
        if (objectIds.Count == 0) return activeLayers;

        // Query msdyn_componentlayer for each component individually
        // The virtual entity works best with single component queries (like the UI does)
        var tasks = new List<Task<(Guid ObjectId, Entity? ActiveLayer)>>();
        var semaphore = new SemaphoreSlim(10); // Limit concurrent requests

        foreach (var objectId in objectIds)
        {
            tasks.Add(GetActiveLayerForComponentAsync(objectId, solutionComponentName, semaphore));
        }

        var results = await Task.WhenAll(tasks);

        foreach (var (objectId, activeLayer) in results)
        {
            if (activeLayer != null)
            {
                activeLayers[objectId] = activeLayer;
            }
        }

        return activeLayers;
    }

    private async Task<(Guid ObjectId, Entity? ActiveLayer)> GetActiveLayerForComponentAsync(
        Guid objectId, string solutionComponentName, SemaphoreSlim semaphore)
    {
        await semaphore.WaitAsync();
        try
        {
            // Use FetchXML - virtual entities sometimes work better with this
            var fetchXml = $@"
                <fetch>
                    <entity name='msdyn_componentlayer'>
                        <attribute name='msdyn_componentid' />
                        <attribute name='msdyn_name' />
                        <attribute name='msdyn_solutionname' />
                        <attribute name='msdyn_solutioncomponentname' />
                        <attribute name='msdyn_order' />
                        <attribute name='msdyn_overwritetime' />
                        <attribute name='msdyn_publishername' />
                        <filter type='and'>
                            <condition attribute='msdyn_componentid' operator='eq' value='{objectId}' />
                            <condition attribute='msdyn_solutioncomponentname' operator='eq' value='{solutionComponentName}' />
                        </filter>
                    </entity>
                </fetch>";

            var result = await _dataverseService.RetrieveMultipleAsync(new FetchExpression(fetchXml));

            // Debug: log the first few queries
            if (result.Entities.Count > 0)
            {
                Console.WriteLine($"\n    [DEBUG] Found {result.Entities.Count} layers for {solutionComponentName} {objectId}");
                foreach (var layer in result.Entities)
                {
                    var solName = layer.GetAttributeValue<string>("msdyn_solutionname") ?? "null";
                    var compName = layer.GetAttributeValue<string>("msdyn_name") ?? "null";
                    Console.WriteLine($"      - Solution: {solName}, Name: {compName}");
                }
            }

            // Find the Active layer if it exists
            foreach (var layer in result.Entities)
            {
                var solutionName = layer.GetAttributeValue<string>("msdyn_solutionname") ?? "";
                if (solutionName == "Active")
                {
                    return (objectId, layer);
                }
            }

            return (objectId, null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n    [ERROR] Query failed for {solutionComponentName} {objectId}: {ex.Message}");
            return (objectId, null);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task EnrichComponentNamesAsync(
        List<(Entity Component, Entity Layer, int ComponentType, Guid ObjectId, string LogicalName)> matches)
    {
        // Group by component type for efficient batched lookups
        var byType = matches.GroupBy(m => m.ComponentType);

        foreach (var group in byType)
        {
            var componentType = group.Key;
            var items = group.ToList();
            var objectIds = items.Select(i => i.ObjectId).ToList();

            var nameMap = await FetchNamesForComponentTypeAsync(componentType, objectIds);

            foreach (var (component, layer, _, objectId, _) in items)
            {
                if (nameMap.TryGetValue(objectId, out var name))
                {
                    layer["msdyn_name"] = name;
                }
                else
                {
                    // Use the msdyn_name from layer, or generate a descriptive fallback
                    var existingName = layer.GetAttributeValue<string>("msdyn_name");
                    if (string.IsNullOrEmpty(existingName) || Guid.TryParse(existingName, out _))
                    {
                        layer["msdyn_name"] = $"{ComponentTypeHelper.GetComponentTypeName(componentType)}: {objectId}";
                    }
                }
            }
        }
    }

    private async Task<Dictionary<Guid, string>> FetchNamesForComponentTypeAsync(int componentType, List<Guid> objectIds)
    {
        var names = new Dictionary<Guid, string>();
        if (objectIds.Count == 0) return names;

        try
        {
            switch (componentType)
            {
                case 61: // Web Resource
                    await FetchNamesFromTableAsync("webresource", "webresourceid", "name", objectIds, names, "Web Resource");
                    break;
                case 20: // Security Role
                    await FetchNamesFromTableAsync("role", "roleid", "name", objectIds, names, "Role");
                    break;
                case 29: // Workflow
                    await FetchNamesFromTableAsync("workflow", "workflowid", "name", objectIds, names, "Workflow");
                    break;
                case 380: // Environment Variable Definition
                    await FetchNamesFromTableAsync("environmentvariabledefinition", "environmentvariabledefinitionid", "displayname", objectIds, names, "Environment Variable Definition");
                    break;
                case 381: // Environment Variable Value
                    await FetchEnvVarValueNamesAsync(objectIds, names);
                    break;
                case 80: // Model-driven App
                    await FetchNamesFromTableAsync("appmodule", "appmoduleid", "name", objectIds, names, "App");
                    break;
                case 300: // Canvas App
                    await FetchNamesFromTableAsync("canvasapp", "canvasappid", "name", objectIds, names, "Canvas App");
                    break;
                case 91: // Plugin Assembly
                    await FetchNamesFromTableAsync("pluginassembly", "pluginassemblyid", "name", objectIds, names, "Plugin Assembly");
                    break;
                case 92: // SDK Message Processing Step
                    await FetchNamesFromTableAsync("sdkmessageprocessingstep", "sdkmessageprocessingstepid", "name", objectIds, names, "Plugin Step");
                    break;
                case 62: // Site Map
                    await FetchNamesFromTableAsync("sitemap", "sitemapid", "sitemapname", objectIds, names, "Site Map");
                    break;
                case 63: // Connection Role
                    await FetchNamesFromTableAsync("connectionrole", "connectionroleid", "name", objectIds, names, "Connection Role");
                    break;
                case 9: // Option Set
                    await FetchOptionSetNamesForEnrichAsync(objectIds, names);
                    break;
                case 10112: // Connection Reference
                    await FetchNamesFromTableAsync("connectionreference", "connectionreferenceid", "connectionreferencelogicalname", objectIds, names, "Connection Reference");
                    break;
                case 10140: // Custom API
                    await FetchNamesFromTableAsync("customapi", "customapiid", "name", objectIds, names, "Custom API");
                    break;
                case 371: // Connector
                    await FetchNamesFromTableAsync("connector", "connectorid", "name", objectIds, names, "Connector");
                    break;
                default:
                    // For unknown types, return empty - we'll use fallback
                    break;
            }
        }
        catch { /* Silently fail - names will use fallback */ }

        return names;
    }

    private async Task FetchNamesFromTableAsync(string tableName, string idColumn, string nameColumn,
        List<Guid> objectIds, Dictionary<Guid, string> names, string typePrefix)
    {
        var query = new QueryExpression(tableName)
        {
            ColumnSet = new ColumnSet(idColumn, nameColumn),
            Criteria = new FilterExpression
            {
                Conditions = { new ConditionExpression(idColumn, ConditionOperator.In, objectIds.Cast<object>().ToArray()) }
            }
        };

        var result = await _dataverseService.RetrieveMultipleAsync(query);
        foreach (var entity in result.Entities)
        {
            var id = entity.GetAttributeValue<Guid>(idColumn);
            var name = entity.GetAttributeValue<string>(nameColumn) ?? id.ToString();
            names[id] = $"{typePrefix}: {name}";
        }
    }

    private async Task FetchEnvVarValueNamesAsync(List<Guid> objectIds, Dictionary<Guid, string> names)
    {
        // Environment variable values don't have a name, so look up the parent definition
        var query = new QueryExpression("environmentvariablevalue")
        {
            ColumnSet = new ColumnSet("environmentvariablevalueid", "environmentvariabledefinitionid"),
            Criteria = new FilterExpression
            {
                Conditions = { new ConditionExpression("environmentvariablevalueid", ConditionOperator.In, objectIds.Cast<object>().ToArray()) }
            }
        };
        query.LinkEntities.Add(new LinkEntity
        {
            LinkFromEntityName = "environmentvariablevalue",
            LinkFromAttributeName = "environmentvariabledefinitionid",
            LinkToEntityName = "environmentvariabledefinition",
            LinkToAttributeName = "environmentvariabledefinitionid",
            JoinOperator = JoinOperator.LeftOuter,
            Columns = new ColumnSet("displayname"),
            EntityAlias = "def"
        });

        var result = await _dataverseService.RetrieveMultipleAsync(query);
        foreach (var entity in result.Entities)
        {
            var id = entity.GetAttributeValue<Guid>("environmentvariablevalueid");
            var defName = entity.GetAttributeValue<AliasedValue>("def.displayname")?.Value as string ?? id.ToString();
            names[id] = $"Environment Variable Value: {defName}";
        }
    }

    private async Task FetchOptionSetNamesForEnrichAsync(List<Guid> objectIds, Dictionary<Guid, string> names)
    {
        var request = new RetrieveAllOptionSetsRequest();
        var response = (RetrieveAllOptionSetsResponse)await _dataverseService.ExecuteAsync(request);

        var idSet = objectIds.ToHashSet();
        foreach (var optionSet in response.OptionSetMetadata)
        {
            if (optionSet.MetadataId.HasValue && idSet.Contains(optionSet.MetadataId.Value))
            {
                var displayName = optionSet.DisplayName?.UserLocalizedLabel?.Label ?? optionSet.Name;
                names[optionSet.MetadataId.Value] = $"Option Set: {displayName}";
            }
        }
    }

    private async Task<List<(Entity Component, Entity Layer, string LogicalName)>> GetEntitySubcomponentsWithActiveLayersAsync(
        HashSet<string> entityNames)
    {
        var results = new List<(Entity Component, Entity Layer, string LogicalName)>();

        // Check forms (type 60)
        Console.Write("\r  Checking forms with Active layers...                    ");
        await CheckEntitySubcomponentsForActiveLayers("systemform", "formid", "objecttypecode", "name",
            entityNames, "SystemForm", 60, results);

        // Check views (type 26)
        Console.Write("\r  Checking views with Active layers...                    ");
        await CheckEntitySubcomponentsForActiveLayers("savedquery", "savedqueryid", "returnedtypecode", "name",
            entityNames, "SavedQuery", 26, results);

        // Check charts (type 59)
        Console.Write("\r  Checking charts with Active layers...                   ");
        await CheckEntitySubcomponentsForActiveLayers("savedqueryvisualization", "savedqueryvisualizationid",
            "primaryentitytypecode", "name", entityNames, "SavedQueryVisualization", 59, results);

        // Check attributes (type 2)
        Console.Write("\r  Checking attributes with Active layers...               ");
        await CheckAttributesForActiveLayers(entityNames, results);

        return results;
    }

    private async Task CheckEntitySubcomponentsForActiveLayers(
        string tableName, string idColumn, string entityColumn, string nameColumn,
        HashSet<string> entityNames, string solutionComponentName,
        int componentType, List<(Entity Component, Entity Layer, string LogicalName)> results)
    {
        var query = new QueryExpression(tableName)
        {
            ColumnSet = new ColumnSet(idColumn, nameColumn, entityColumn),
            Criteria = new FilterExpression
            {
                Conditions = { new ConditionExpression(entityColumn, ConditionOperator.In, entityNames.ToArray()) }
            },
            PageInfo = new PagingInfo { Count = 5000, PageNumber = 1 }
        };

        try
        {
            var entities = await _dataverseService.RetrieveAllAsync(query);

            if (entities.Count == 0) return;

            // Get IDs and query for Active layers
            var objectIds = entities.Select(e => e.GetAttributeValue<Guid>(idColumn)).Where(id => id != Guid.Empty).ToList();
            var activeLayers = await GetActiveLayersForComponentsAsync(objectIds, solutionComponentName);

            foreach (var entity in entities)
            {
                var id = entity.GetAttributeValue<Guid>(idColumn);
                var name = entity.GetAttributeValue<string>(nameColumn) ?? "Unknown";
                var entityName = entity.GetAttributeValue<string>(entityColumn) ?? "Unknown";

                // Only include if it has an Active layer
                if (activeLayers.TryGetValue(id, out var layer))
                {
                    var comp = CreateSubcomponentEntity(id, componentType, $"{entityName}.{name}", entityName);
                    // Update layer with proper name
                    layer["msdyn_name"] = $"{entityName}.{name}";
                    results.Add((comp, layer, tableName));
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\r  Warning: Error checking {tableName}: {ex.Message}");
        }
    }

    private async Task CheckAttributesForActiveLayers(
        HashSet<string> entityNames,
        List<(Entity Component, Entity Layer, string LogicalName)> results)
    {
        try
        {
            var request = new RetrieveAllEntitiesRequest
            {
                EntityFilters = EntityFilters.Attributes,
                RetrieveAsIfPublished = false
            };

            var response = (RetrieveAllEntitiesResponse)await _dataverseService.ExecuteAsync(request);

            // Collect all attribute IDs for entities we care about
            var attributeInfo = new List<(Guid AttrId, string EntityName, string AttrName)>();

            foreach (var entityMetadata in response.EntityMetadata)
            {
                if (!entityNames.Contains(entityMetadata.LogicalName))
                    continue;

                foreach (var attr in entityMetadata.Attributes)
                {
                    var attrId = attr.MetadataId ?? Guid.Empty;
                    if (attrId != Guid.Empty)
                    {
                        attributeInfo.Add((attrId, entityMetadata.LogicalName, attr.LogicalName));
                    }
                }
            }

            if (attributeInfo.Count == 0) return;

            // Query for Active layers in batches
            var objectIds = attributeInfo.Select(a => a.AttrId).ToList();
            var activeLayers = await GetActiveLayersForComponentsAsync(objectIds, "Attribute");

            foreach (var (attrId, entityName, attrName) in attributeInfo)
            {
                if (activeLayers.TryGetValue(attrId, out var layer))
                {
                    var name = $"{entityName}.{attrName}";
                    var comp = CreateSubcomponentEntity(attrId, 2, name, entityName);
                    layer["msdyn_name"] = name;
                    results.Add((comp, layer, "attribute"));
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\r  Warning: Error checking attributes: {ex.Message}");
        }
    }

    private async Task<Entity?> GetActiveSolutionAsync()
    {
        var query = new QueryExpression("solution")
        {
            ColumnSet = new ColumnSet("solutionid"),
            Criteria = new FilterExpression
            {
                Conditions = { new ConditionExpression("uniquename", ConditionOperator.Equal, "Active") }
            },
            TopCount = 1
        };

        var result = await _dataverseService.RetrieveMultipleAsync(query);
        return result.Entities.FirstOrDefault();
    }

    private HashSet<string> GetManagedEntityNames(List<Entity> components, Dictionary<Guid, string> entityMetadataMap)
    {
        var entityNames = new HashSet<string>();

        foreach (var comp in components)
        {
            var compType = comp.GetAttributeValue<OptionSetValue>("componenttype")?.Value ?? 0;
            if (compType == 1) // Entity
            {
                var objectId = comp.GetAttributeValue<Guid>("objectid");
                if (entityMetadataMap.TryGetValue(objectId, out var entityName))
                {
                    entityNames.Add(entityName);
                }
            }
        }

        return entityNames;
    }

    private Entity CreateSubcomponentEntity(Guid id, int componentType, string name, string entityName)
    {
        var comp = new Entity("solutioncomponent");
        comp["objectid"] = id;
        comp["componenttype"] = new OptionSetValue(componentType);
        comp["_componentname"] = name;
        comp["_entityname"] = entityName;
        return comp;
    }

    private async Task<List<Entity>> GetUnmanagedPowerPagesComponentsAsync(List<Entity> powerPagesComponents)
    {
        if (powerPagesComponents.Count == 0)
            return new List<Entity>();

        var unmanagedComponents = new List<Entity>();

        try
        {
            // Get the Active Solution ID
            Console.Write("\r  Finding Active Solution for Power Pages...              ");
            var activeSolution = await GetActiveSolutionAsync();
            if (activeSolution == null)
            {
                Console.WriteLine("\r  Active Solution not found.                              ");
                return unmanagedComponents;
            }

            var activeSolutionId = activeSolution.GetAttributeValue<Guid>("solutionid");

            // Query powerpagecomponent table for unmanaged customizations
            // Components with solutionid = Active Solution represent unmanaged customizations
            Console.Write("\r  Querying powerpagecomponent table...                    ");

            var ppQuery = new QueryExpression("powerpagecomponent")
            {
                ColumnSet = new ColumnSet("powerpagecomponentid", "name", "powerpagecomponenttype",
                    "solutionid", "modifiedby", "modifiedon"),
                Criteria = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression("solutionid", ConditionOperator.Equal, activeSolutionId)
                    }
                },
                PageInfo = new PagingInfo { Count = 5000, PageNumber = 1 }
            };

            var allActiveComponents = await _dataverseService.RetrieveAllAsync(ppQuery, (page, count) =>
            {
                Console.Write($"\r  Querying powerpagecomponent... (page {page}, {count} found)    ");
            });

            Console.WriteLine($"\r  Found {allActiveComponents.Count} Power Pages components in Active Solution.          ");

            // Get the component IDs from the managed solution we're checking
            var solutionComponentIds = new HashSet<Guid>(
                powerPagesComponents
                    .Select(c => c.GetAttributeValue<Guid>("objectid"))
                    .Where(id => id != Guid.Empty)
            );

            // Filter to only components that are in our target managed solution
            foreach (var ppComponent in allActiveComponents)
            {
                var ppId = ppComponent.GetAttributeValue<Guid>("powerpagecomponentid");
                if (solutionComponentIds.Contains(ppId))
                {
                    unmanagedComponents.Add(ppComponent);
                }
            }

            Console.WriteLine($"  Found {unmanagedComponents.Count} Power Pages components with unmanaged customizations.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Error checking Power Pages: {ex.Message}");
        }

        return unmanagedComponents;
    }

    private async Task<int> ProcessStandardComponentsAsync(
        List<(Entity Component, Entity Layer, string LogicalName)> matchingLayers,
        List<ComponentResult> componentResults,
        bool exportOnly,
        ProcessingState state)
    {
        int layersRemoved = 0;

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
                if (i == 0)
                    Console.WriteLine($"Collecting {matchingLayers.Count} standard components for export...");
            }
            else
            {
                (wasRemoved, removalStatus, layersRemoved) = await HandleComponentRemovalAsync(
                    layer, logicalName, componentType, state, layersRemoved);
            }

            componentResults.Add(new ComponentResult(
                ComponentName: componentName,
                ComponentType: ComponentTypeHelper.GetComponentTypeName(componentType),
                ComponentId: Guid.TryParse(componentId, out var id) ? id : Guid.Empty,
                EntityName: entityName,
                SolutionLayer: "Active",
                ModifiedOn: modifiedOn,
                ModifiedBy: modifiedBy,
                WasRemoved: wasRemoved,
                RemovalStatus: removalStatus
            ));
        }

        return layersRemoved;
    }

    private async Task<int> ProcessPowerPagesComponentsAsync(
        List<Entity> unmanagedComponents,
        List<ComponentResult> componentResults,
        bool exportOnly,
        ProcessingState state)
    {
        if (unmanagedComponents.Count == 0)
            return 0;

        int removed = 0;

        if (!exportOnly && !state.SkipAll && !state.RemoveAll)
        {
            Console.WriteLine();
            Console.WriteLine("===========================================");
            Console.WriteLine("  POWER PAGES UNMANAGED CUSTOMIZATIONS");
            Console.WriteLine("===========================================");
        }
        else if (exportOnly)
        {
            Console.WriteLine($"Collecting {unmanagedComponents.Count} Power Pages components for export...");
        }

        foreach (var ppComponent in unmanagedComponents)
        {
            var componentName = ppComponent.GetAttributeValue<string>("name") ?? "Unknown";
            var ppComponentType = ppComponent.GetAttributeValue<OptionSetValue>("powerpagecomponenttype");
            var componentTypeName = ppComponentType != null
                ? ComponentTypeHelper.GetPowerPagesComponentTypeName(ppComponentType.Value)
                : "Unknown";
            var componentId = ppComponent.GetAttributeValue<Guid>("powerpagecomponentid");
            var modifiedOn = ppComponent.GetAttributeValue<DateTime?>("modifiedon");
            var modifiedByRef = ppComponent.GetAttributeValue<EntityReference>("modifiedby");
            var modifiedBy = modifiedByRef?.Name ?? "Unknown";

            bool wasRemoved = false;
            string removalStatus = "Not Removed";

            if (!exportOnly)
            {
                (wasRemoved, removalStatus, removed) = await HandlePowerPagesRemovalAsync(
                    ppComponent, state, removed);
            }

            componentResults.Add(new ComponentResult(
                ComponentName: componentName,
                ComponentType: $"Power Pages - {componentTypeName}",
                ComponentId: componentId,
                EntityName: "",
                SolutionLayer: "Active",
                ModifiedOn: modifiedOn,
                ModifiedBy: modifiedBy,
                WasRemoved: wasRemoved,
                RemovalStatus: removalStatus
            ));
        }

        return removed;
    }

    private async Task<(bool WasRemoved, string Status, int TotalRemoved)> HandleComponentRemovalAsync(
        Entity layer, string logicalName, int componentType,
        ProcessingState state, int totalRemoved)
    {
        DisplayLayerInfo(layer, componentType);

        if (state.SkipAll)
            return (false, "Skipped (Skip All)", totalRemoved);

        if (state.RemoveAll)
        {
            Console.WriteLine("Auto-removing unmanaged layer...");
            var removed = await RemoveUnmanagedLayerAsync(layer, logicalName);
            Console.WriteLine();
            return removed
                ? (true, "Removed", totalRemoved + 1)
                : (false, "Removal Failed", totalRemoved);
        }

        Console.Write("Do you want to remove this unmanaged layer? (y/n/a=all/s=skip all): ");
        var response = Console.ReadLine()?.Trim().ToLower();

        switch (response)
        {
            case "s":
                Console.WriteLine("Skipping remaining layers.");
                state.SkipAll = true;
                Console.WriteLine();
                return (false, "Skipped (Skip All)", totalRemoved);

            case "a":
                state.RemoveAll = true;
                var removedA = await RemoveUnmanagedLayerAsync(layer, logicalName);
                Console.WriteLine();
                return removedA
                    ? (true, "Removed", totalRemoved + 1)
                    : (false, "Removal Failed", totalRemoved);

            case "y":
                var removedY = await RemoveUnmanagedLayerAsync(layer, logicalName);
                Console.WriteLine();
                return removedY
                    ? (true, "Removed", totalRemoved + 1)
                    : (false, "Removal Failed", totalRemoved);

            default:
                Console.WriteLine("Skipped.");
                Console.WriteLine();
                return (false, "Skipped", totalRemoved);
        }
    }

    private async Task<(bool WasRemoved, string Status, int TotalRemoved)> HandlePowerPagesRemovalAsync(
        Entity ppComponent, ProcessingState state, int totalRemoved)
    {
        DisplayPowerPagesComponentInfo(ppComponent);

        if (state.SkipAll)
            return (false, "Skipped (Skip All)", totalRemoved);

        if (state.RemoveAll)
        {
            Console.WriteLine("Auto-removing Power Pages unmanaged customization...");
            var removed = await RemovePowerPagesCustomizationAsync(ppComponent);
            Console.WriteLine();
            return removed
                ? (true, "Removed", totalRemoved + 1)
                : (false, "Removal Failed", totalRemoved);
        }

        Console.Write("Do you want to remove this unmanaged customization? (y/n/a=all/s=skip all): ");
        var response = Console.ReadLine()?.Trim().ToLower();

        switch (response)
        {
            case "s":
                Console.WriteLine("Skipping remaining components.");
                state.SkipAll = true;
                Console.WriteLine();
                return (false, "Skipped (Skip All)", totalRemoved);

            case "a":
                state.RemoveAll = true;
                var removedA = await RemovePowerPagesCustomizationAsync(ppComponent);
                Console.WriteLine();
                return removedA
                    ? (true, "Removed", totalRemoved + 1)
                    : (false, "Removal Failed", totalRemoved);

            case "y":
                var removedY = await RemovePowerPagesCustomizationAsync(ppComponent);
                Console.WriteLine();
                return removedY
                    ? (true, "Removed", totalRemoved + 1)
                    : (false, "Removal Failed", totalRemoved);

            default:
                Console.WriteLine("Skipped.");
                Console.WriteLine();
                return (false, "Skipped", totalRemoved);
        }
    }

    private void DisplayLayerInfo(Entity layer, int componentType)
    {
        Console.WriteLine();
        Console.WriteLine("===========================================");
        Console.WriteLine("  UNMANAGED LAYER FOUND");
        Console.WriteLine("===========================================");

        var componentName = layer.GetAttributeValue<string>("msdyn_name") ?? "Unknown";
        var solutionName = layer.GetAttributeValue<string>("msdyn_solutionname") ?? "Active";
        var modifiedOn = layer.GetAttributeValue<DateTime?>("msdyn_overwritetime");
        var modifiedBy = layer.GetAttributeValue<string>("msdyn_publishername") ?? "Unknown";
        var order = layer.GetAttributeValue<int>("msdyn_order");

        Console.WriteLine($"  Component Name:  {componentName}");
        Console.WriteLine($"  Component Type:  {ComponentTypeHelper.GetComponentTypeName(componentType)}");
        Console.WriteLine($"  Solution Layer:  {solutionName}");
        Console.WriteLine($"  Layer Order:     {order}");
        Console.WriteLine($"  Modified On:     {modifiedOn?.ToString("yyyy-MM-dd HH:mm:ss") ?? "N/A"}");
        Console.WriteLine($"  Modified By:     {modifiedBy}");
        Console.WriteLine("-------------------------------------------");
    }

    private void DisplayPowerPagesComponentInfo(Entity ppComponent)
    {
        Console.WriteLine();
        Console.WriteLine("===========================================");
        Console.WriteLine("  POWER PAGES UNMANAGED CUSTOMIZATION");
        Console.WriteLine("===========================================");

        var componentName = ppComponent.GetAttributeValue<string>("name") ?? "Unknown";
        var componentType = ppComponent.GetAttributeValue<OptionSetValue>("powerpagecomponenttype");
        var componentTypeName = componentType != null
            ? ComponentTypeHelper.GetPowerPagesComponentTypeName(componentType.Value)
            : "Unknown";
        var modifiedOn = ppComponent.GetAttributeValue<DateTime?>("modifiedon");
        var modifiedByRef = ppComponent.GetAttributeValue<EntityReference>("modifiedby");
        var modifiedBy = modifiedByRef?.Name ?? "Unknown";
        var componentId = ppComponent.GetAttributeValue<Guid>("powerpagecomponentid");

        Console.WriteLine($"  Component Name:  {componentName}");
        Console.WriteLine($"  Component ID:    {componentId}");
        Console.WriteLine($"  Component Type:  {componentTypeName}");
        Console.WriteLine($"  Modified On:     {modifiedOn?.ToString("yyyy-MM-dd HH:mm:ss") ?? "N/A"}");
        Console.WriteLine($"  Modified By:     {modifiedBy}");
        Console.WriteLine("-------------------------------------------");
    }

    private async Task<bool> RemoveUnmanagedLayerAsync(Entity layer, string solutionComponentName)
    {
        try
        {
            var componentId = layer.GetAttributeValue<string>("msdyn_componentid") ?? "";

            if (!Guid.TryParse(componentId, out var objectId))
            {
                Console.WriteLine("Error: Invalid component ID.");
                return false;
            }

            Console.WriteLine("Removing unmanaged layer...");

            var request = new OrganizationRequest("RemoveActiveCustomizations")
            {
                Parameters =
                {
                    { "SolutionComponentName", solutionComponentName },
                    { "ComponentId", objectId }
                }
            };

            await _dataverseService.ExecuteAsync(request);
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
            Console.WriteLine($"Error removing layer: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> RemovePowerPagesCustomizationAsync(Entity ppComponent)
    {
        try
        {
            var componentId = ppComponent.GetAttributeValue<Guid>("powerpagecomponentid");

            Console.WriteLine("Removing Power Pages unmanaged customization...");

            var request = new OrganizationRequest("RemoveActiveCustomization")
            {
                Parameters =
                {
                    { "SolutionComponentName", "powerpagecomponent" },
                    { "ComponentId", componentId }
                }
            };

            await _dataverseService.ExecuteAsync(request);
            Console.WriteLine("Unmanaged customization removed successfully!");
            return true;
        }
        catch (FaultException<OrganizationServiceFault> ex)
        {
            Console.WriteLine($"Error removing customization: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error removing customization: {ex.Message}");
            return false;
        }
    }

    private void PrintSummary(int standardCount, int powerPagesCount, int totalRemoved)
    {
        Console.WriteLine("-------------------------------------------");
        Console.WriteLine($"Summary:");
        Console.WriteLine($"  Standard components with unmanaged layers: {standardCount}");
        Console.WriteLine($"  Power Pages components with unmanaged customizations: {powerPagesCount}");
        Console.WriteLine($"  Total unmanaged layers removed: {totalRemoved}");
    }
}
