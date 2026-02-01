using DataverseUnmanagedLayerFixer.Helpers;
using DataverseUnmanagedLayerFixer.Models;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using System.ServiceModel;

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

        // Get managed entity names for subcomponent lookup
        var managedEntityNames = GetManagedEntityNames(components, entityMetadataMap);
        Console.WriteLine($"  Found {managedEntityNames.Count} entities in managed solution.");

        // Build list of all components to check (explicit components + entity subcomponents)
        var componentsToCheck = new List<(Guid ObjectId, int ComponentType, string LogicalName, Entity OriginalComponent)>();

        // Add explicit non-entity components
        foreach (var component in components)
        {
            var componentType = component.GetAttributeValue<OptionSetValue>("componenttype")?.Value ?? 0;
            var objectId = component.GetAttributeValue<Guid>("objectid");

            if (componentType == 1) continue; // Skip entities - check their subcomponents instead
            if (objectId == Guid.Empty) continue;

            var logicalName = ComponentTypeHelper.GetSolutionComponentLogicalName(componentType) ?? "unknown";
            componentsToCheck.Add((objectId, componentType, logicalName, component));
        }

        // Add entity subcomponents (forms, views, charts, attributes)
        if (managedEntityNames.Count > 0)
        {
            var subcomponents = await GetEntitySubcomponentsToCheckAsync(managedEntityNames, components);
            componentsToCheck.AddRange(subcomponents);
        }

        Console.WriteLine($"  Checking {componentsToCheck.Count} components for Active layers...");

        // Use RetrieveSolutionComponentLayers API to check each component for Active layer
        int checkedCount = 0;
        int foundCount = 0;
        foreach (var (objectId, componentType, logicalName, originalComponent) in componentsToCheck)
        {
            checkedCount++;
            if (checkedCount % 50 == 0 || checkedCount == componentsToCheck.Count)
            {
                Console.Write($"\r  Checking layers... ({checkedCount}/{componentsToCheck.Count}, found {foundCount})    ");
            }

            var activeLayer = await GetActiveLayerAsync(objectId, logicalName);
            if (activeLayer != null)
            {
                foundCount++;
                results.Add((originalComponent, activeLayer, logicalName));
            }
        }

        Console.WriteLine($"\r  Found {foundCount} components with Active layers.                    ");

        return results;
    }

    private async Task<Entity?> GetActiveLayerAsync(Guid componentId, string solutionComponentName)
    {
        try
        {
            var request = new OrganizationRequest("RetrieveSolutionComponentLayers")
            {
                Parameters =
                {
                    { "SolutionComponentName", solutionComponentName },
                    { "ComponentId", componentId }
                }
            };

            var response = await _dataverseService.ExecuteAsync(request);

            if (response.Results.TryGetValue("SolutionComponentLayers", out var layersObj) &&
                layersObj is EntityCollection layers)
            {
                // Look for the Active layer (msdyn_solutionname == "Active")
                foreach (var layer in layers.Entities)
                {
                    var solutionName = layer.GetAttributeValue<string>("msdyn_solutionname");
                    if (solutionName == "Active")
                    {
                        return layer;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Log first few errors to help debug
            if (_layerApiErrorCount < 3)
            {
                Console.WriteLine($"\n  [DEBUG] Layer API error for {solutionComponentName}/{componentId}: {ex.Message}");
                _layerApiErrorCount++;
            }
        }

        return null;
    }

    private int _layerApiErrorCount = 0;

    private async Task<List<(Guid ObjectId, int ComponentType, string LogicalName, Entity Component)>> GetEntitySubcomponentsToCheckAsync(
        HashSet<string> entityNames, List<Entity> managedComponents)
    {
        var results = new List<(Guid ObjectId, int ComponentType, string LogicalName, Entity Component)>();

        // Build set of managed component IDs for filtering
        var managedComponentIds = managedComponents
            .Select(c => c.GetAttributeValue<Guid>("objectid"))
            .Where(id => id != Guid.Empty)
            .ToHashSet();

        // Check forms
        Console.Write("\r  Loading forms...                                        ");
        var forms = await GetSubcomponentRecordsAsync("systemform", "formid", "objecttypecode", "name", entityNames, managedComponentIds);
        foreach (var (id, name, entityName) in forms)
        {
            var comp = CreateSubcomponentEntity(id, 60, name, entityName);
            results.Add((id, 60, "systemform", comp));
        }

        // Check views
        Console.Write("\r  Loading views...                                        ");
        var views = await GetSubcomponentRecordsAsync("savedquery", "savedqueryid", "returnedtypecode", "name", entityNames, managedComponentIds);
        foreach (var (id, name, entityName) in views)
        {
            var comp = CreateSubcomponentEntity(id, 26, name, entityName);
            results.Add((id, 26, "savedquery", comp));
        }

        // Check charts
        Console.Write("\r  Loading charts...                                       ");
        var charts = await GetSubcomponentRecordsAsync("savedqueryvisualization", "savedqueryvisualizationid", "primaryentitytypecode", "name", entityNames, managedComponentIds);
        foreach (var (id, name, entityName) in charts)
        {
            var comp = CreateSubcomponentEntity(id, 59, name, entityName);
            results.Add((id, 59, "savedqueryvisualization", comp));
        }

        // Check attributes
        Console.Write("\r  Loading attributes...                                   ");
        var attributes = await GetEntityAttributesToCheckAsync(entityNames, managedComponentIds);
        foreach (var (id, name, entityName) in attributes)
        {
            var comp = CreateSubcomponentEntity(id, 2, name, entityName);
            results.Add((id, 2, "attribute", comp));
        }

        return results;
    }

    private async Task<List<(Guid Id, string Name, string EntityName)>> GetSubcomponentRecordsAsync(
        string tableName, string idColumn, string entityColumn, string nameColumn,
        HashSet<string> entityNames, HashSet<Guid> managedComponentIds)
    {
        var results = new List<(Guid Id, string Name, string EntityName)>();

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

            foreach (var entity in entities)
            {
                var id = entity.GetAttributeValue<Guid>(idColumn);
                var name = entity.GetAttributeValue<string>(nameColumn) ?? "Unknown";
                var entityName = entity.GetAttributeValue<string>(entityColumn) ?? "Unknown";

                // Only include if it's in the managed solution
                if (managedComponentIds.Contains(id))
                {
                    results.Add((id, $"{entityName}.{name}", entityName));
                }
            }
        }
        catch { /* Silently fail */ }

        return results;
    }

    private async Task<List<(Guid Id, string Name, string EntityName)>> GetEntityAttributesToCheckAsync(
        HashSet<string> entityNames, HashSet<Guid> managedComponentIds)
    {
        var results = new List<(Guid Id, string Name, string EntityName)>();

        try
        {
            var request = new RetrieveAllEntitiesRequest
            {
                EntityFilters = EntityFilters.Attributes,
                RetrieveAsIfPublished = false
            };

            var response = (RetrieveAllEntitiesResponse)await _dataverseService.ExecuteAsync(request);

            foreach (var entityMetadata in response.EntityMetadata)
            {
                if (!entityNames.Contains(entityMetadata.LogicalName))
                    continue;

                foreach (var attr in entityMetadata.Attributes)
                {
                    var attrId = attr.MetadataId ?? Guid.Empty;
                    // Only include if it's in the managed solution
                    if (attrId != Guid.Empty && managedComponentIds.Contains(attrId))
                    {
                        var displayName = attr.DisplayName?.UserLocalizedLabel?.Label ?? attr.LogicalName;
                        results.Add((attrId, $"{entityMetadata.LogicalName}.{displayName}", entityMetadata.LogicalName));
                    }
                }
            }
        }
        catch { /* Silently fail */ }

        return results;
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

    private async Task<Dictionary<Guid, string>> GetComponentDisplayNamesAsync(
        List<(Entity Component, int ComponentType, Guid ObjectId, string LogicalName)> components,
        Dictionary<Guid, string> entityMetadataMap)
    {
        var names = new Dictionary<Guid, string>();
        var groupedByType = components.GroupBy(c => c.ComponentType);

        foreach (var group in groupedByType)
        {
            var componentType = group.Key;
            var objectIds = group.Select(c => c.ObjectId).ToList();

            try
            {
                switch (componentType)
                {
                    case 60: // System Form
                        await FetchComponentNamesAsync("systemform", "formid", "name", objectIds, names, "Form");
                        break;
                    case 26: // Saved Query
                        await FetchComponentNamesAsync("savedquery", "savedqueryid", "name", objectIds, names, "View");
                        break;
                    case 59: // Chart
                        await FetchComponentNamesAsync("savedqueryvisualization", "savedqueryvisualizationid", "name", objectIds, names, "Chart");
                        break;
                    case 61: // Web Resource
                        await FetchComponentNamesAsync("webresource", "webresourceid", "name", objectIds, names, "Web Resource");
                        break;
                    case 29: // Workflow
                        await FetchComponentNamesAsync("workflow", "workflowid", "name", objectIds, names, "Workflow");
                        break;
                    case 20: // Security Role
                        await FetchComponentNamesAsync("role", "roleid", "name", objectIds, names, "Role");
                        break;
                    case 380: // Environment Variable Definition
                        await FetchComponentNamesAsync("environmentvariabledefinition", "environmentvariabledefinitionid", "displayname", objectIds, names, "Env Variable");
                        break;
                    case 381: // Environment Variable Value
                        await FetchComponentNamesAsync("environmentvariablevalue", "environmentvariablevalueid", "schemaname", objectIds, names, "Env Variable Value");
                        break;
                    case 80: // Model-driven App
                        await FetchComponentNamesAsync("appmodule", "appmoduleid", "name", objectIds, names, "App");
                        break;
                    case 300: // Canvas App
                        await FetchComponentNamesAsync("canvasapp", "canvasappid", "name", objectIds, names, "Canvas App");
                        break;
                    case 91: // Plugin Assembly
                        await FetchComponentNamesAsync("pluginassembly", "pluginassemblyid", "name", objectIds, names, "Plugin Assembly");
                        break;
                    case 92: // SDK Message Processing Step
                        await FetchComponentNamesAsync("sdkmessageprocessingstep", "sdkmessageprocessingstepid", "name", objectIds, names, "Plugin Step");
                        break;
                    case 62: // Site Map
                        await FetchComponentNamesAsync("sitemap", "sitemapid", "sitemapname", objectIds, names, "Site Map");
                        break;
                    case 63: // Connection Role
                        await FetchComponentNamesAsync("connectionrole", "connectionroleid", "name", objectIds, names, "Connection Role");
                        break;
                    case 9: // Option Set
                        await FetchOptionSetNamesAsync(objectIds, names);
                        break;
                    case 2: // Attribute
                        foreach (var (comp, _, objId, _) in group)
                        {
                            var attrName = comp.GetAttributeValue<string>("_componentname");
                            names[objId] = $"Attribute: {attrName ?? objId.ToString()}";
                        }
                        break;
                    default:
                        foreach (var id in objectIds)
                        {
                            names[id] = $"{ComponentTypeHelper.GetComponentTypeName(componentType)}: {id}";
                        }
                        break;
                }
            }
            catch
            {
                foreach (var id in objectIds)
                {
                    if (!names.ContainsKey(id))
                        names[id] = $"{ComponentTypeHelper.GetComponentTypeName(componentType)}: {id}";
                }
            }
        }

        return names;
    }

    private async Task FetchComponentNamesAsync(string tableName, string idColumn, string nameColumn,
        List<Guid> objectIds, Dictionary<Guid, string> names, string typePrefix)
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
            var result = await _dataverseService.RetrieveMultipleAsync(query);
            foreach (var entity in result.Entities)
            {
                var id = entity.GetAttributeValue<Guid>(idColumn);
                var name = entity.GetAttributeValue<string>(nameColumn) ?? id.ToString();
                names[id] = $"{typePrefix}: {name}";
            }
        }
        catch { /* Silently fail */ }

        foreach (var id in objectIds)
        {
            if (!names.ContainsKey(id))
                names[id] = $"{typePrefix}: {id}";
        }
    }

    private async Task FetchOptionSetNamesAsync(List<Guid> objectIds, Dictionary<Guid, string> names)
    {
        if (objectIds.Count == 0) return;

        try
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
        catch { /* Silently fail */ }

        foreach (var id in objectIds)
        {
            if (!names.ContainsKey(id))
                names[id] = $"Option Set: {id}";
        }
    }

    private async Task<List<Entity>> GetUnmanagedPowerPagesComponentsAsync(List<Entity> powerPagesComponents)
    {
        if (powerPagesComponents.Count == 0)
            return new List<Entity>();

        // Power Pages components don't support RetrieveSolutionComponentLayers API well
        // Skip them for now - they require manual verification in the maker portal
        Console.WriteLine($"  Note: Power Pages layer detection is not currently supported.");
        Console.WriteLine($"  Please check Power Pages components manually in the maker portal.");

        return new List<Entity>();
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
