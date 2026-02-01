namespace DataverseUnmanagedLayerFixer.Helpers;

/// <summary>
/// Helper class for component type mappings and lookups.
/// </summary>
public static class ComponentTypeHelper
{
    /// <summary>
    /// Power Pages component types that require special handling.
    /// </summary>
    public static readonly HashSet<int> PowerPagesComponentTypes = new() { 10295, 10296, 10297 };

    /// <summary>
    /// Gets the human-readable name for a solution component type.
    /// </summary>
    public static string GetComponentTypeName(int componentType)
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
            // Modern components (10000+ range)
            10112 => "Connection Reference",
            10140 => "Custom API",
            10141 => "Custom API Request Parameter",
            10142 => "Custom API Response Property",
            10029 => "Flow Machine",
            10030 => "Flow Machine Group",
            10076 => "Desktop Flow Module",
            10039 => "AI Builder Dataset",
            10040 => "AI Builder File",
            10041 => "AI Builder Dataset File",
            10313 => "Catalog Assignment",
            10330 => "Package",
            _ => $"Unknown ({componentType})"
        };
    }

    /// <summary>
    /// Gets the logical name for a component type used in API calls.
    /// </summary>
    public static string? GetSolutionComponentLogicalName(int componentType)
    {
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
            36 => "template",
            37 => "contracttemplate",
            38 => "kbarticletemplate",
            39 => "mailmergetemplate",

            // Reports
            31 => "report",
            32 => "reportentity",
            33 => "reportcategory",
            34 => "reportvisibility",

            // Ribbon
            48 => "ribboncommand",
            49 => "ribboncontextgroup",
            50 => "ribboncustomization",
            52 => "ribbonrule",
            53 => "ribbontabtocommandmap",
            55 => "ribbondiff",

            // Plugins
            90 => "plugintype",
            91 => "pluginassembly",
            92 => "sdkmessageprocessingstep",
            93 => "sdkmessageprocessingstepimage",
            95 => "serviceendpoint",

            // Modern
            300 => "canvasapp",
            371 => "connector",
            380 => "environmentvariabledefinition",
            381 => "environmentvariablevalue",

            // Service components
            150 => "routingrule",
            151 => "routingruleitem",
            152 => "sla",
            153 => "slaitem",
            154 => "convertrule",
            155 => "convertruleitem",

            // Mobile
            161 => "mobileofflineprofile",
            162 => "mobileofflineprofileitem",

            // Modern components
            10112 => "connectionreference",
            10140 => "customapi",
            10141 => "customapirequestparameter",
            10142 => "customapiresponseproperty",

            _ => null
        };
    }

    /// <summary>
    /// Gets the solution component name used in msdyn_solutioncomponentname field.
    /// This is used when querying msdyn_componentlayer.
    /// </summary>
    public static string? GetSolutionComponentName(int componentType)
    {
        // NOTE: These values must NOT have spaces - they match msdyn_solutioncomponentname in Dataverse
        return componentType switch
        {
            1 => "Entity",
            2 => "Attribute",
            3 => "Relationship",
            9 => "OptionSet",
            14 => "EntityKey",
            16 => "Privilege",
            20 => "Role",
            26 => "SavedQuery",
            29 => "Workflow",
            31 => "Report",
            36 => "EmailTemplate",
            44 => "DuplicateRule",
            59 => "SavedQueryVisualization",
            60 => "SystemForm",
            61 => "WebResource",
            62 => "SiteMap",
            63 => "ConnectionRole",
            66 => "CustomControl",
            70 => "FieldSecurityProfile",
            80 => "AppModule",
            90 => "PluginType",
            91 => "PluginAssembly",
            92 => "SDKMessageProcessingStep",
            93 => "SDKMessageProcessingStepImage",
            95 => "ServiceEndpoint",
            150 => "RoutingRule",
            152 => "SLA",
            154 => "ConvertRule",
            161 => "MobileOfflineProfile",
            300 => "CanvasApp",
            371 => "Connector",
            380 => "EnvironmentVariableDefinition",
            381 => "EnvironmentVariableValue",
            // Modern components
            10112 => "ConnectionReference",
            10140 => "CustomAPI",
            10141 => "CustomAPIRequestParameter",
            10142 => "CustomAPIResponseProperty",
            _ => null
        };
    }

    /// <summary>
    /// Gets the human-readable name for a Power Pages component type.
    /// </summary>
    public static string GetPowerPagesComponentTypeName(int componentType)
    {
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
}
