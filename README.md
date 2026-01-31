# Dataverse Unmanaged Layer Fixer

A command-line tool that helps identify and remove unmanaged customization layers from Microsoft Dataverse solutions. This is useful for cleaning up development environments and resolving layer conflicts in Power Platform solutions.

## What It Does

When you customize components in Dataverse (such as entities, forms, views, workflows, etc.), those customizations are stored as "layers." Managed solutions create managed layers, while direct customizations create unmanaged (or "Active") layers. These unmanaged layers can sometimes cause issues when deploying managed solutions or can represent unwanted customizations that need to be cleaned up.

This tool:

1. **Connects to your Dataverse environment** using interactive browser-based authentication
2. **Lists all managed solutions** in your environment (or auto-selects if specified via command line)
3. **Scans all components** within a selected solution
4. **Identifies unmanaged layers** for each component by querying the `msdyn_componentlayer` table
5. **Supports Power Pages components** which use a different tracking mechanism (checks the Default solution)
6. **Displays detailed information** about each unmanaged layer found:
   - Component name and type
   - Solution layer name
   - Layer order
   - Modified date
   - Publisher/modifier information
7. **Prompts you to remove** unwanted unmanaged layers using the `RemoveActiveCustomizations` API
8. **Allows processing multiple solutions** in a single session

## Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later
- Access to a Microsoft Dataverse environment
- Appropriate security privileges to:
  - Read solutions and solution components
  - Query component layers
  - Remove active customizations (System Administrator or System Customizer role recommended)

## Building the Application

1. Clone the repository:
   ```bash
   git clone <repository-url>
   cd dataverse-unmanaged-layer-fixer
   ```

2. Restore NuGet packages and build:
   ```bash
   dotnet restore
   dotnet build
   ```

3. (Optional) Publish as a self-contained executable:
   ```bash
   # For Windows
   dotnet publish -c Release -r win-x64 --self-contained

   # For macOS
   dotnet publish -c Release -r osx-x64 --self-contained

   # For Linux
   dotnet publish -c Release -r linux-x64 --self-contained
   ```

## Usage

### Command-Line Arguments

| Argument | Description |
|----------|-------------|
| `<url>` | The Dataverse environment URL (e.g., `https://yourorg.crm.dynamics.com`) |
| `--solution <name>` or `-s <name>` | (Optional) Auto-select a solution by friendly name or unique name |

### Examples

**Basic usage - interactive solution selection:**
```bash
dotnet run -- https://yourorg.crm.dynamics.com
```

**Specify a solution to check:**
```bash
dotnet run -- https://yourorg.crm.dynamics.com --solution "My Solution Name"
# or using the short form
dotnet run -- https://yourorg.crm.dynamics.com -s MySolutionUniqueName
```

**Running interactively (prompts for URL):**
```bash
dotnet run
```

**Using the published executable:**
```bash
./DataverseUnmanagedLayerFixer https://yourorg.crm.dynamics.com -s MySolution
```

## Walkthrough

1. **Authentication**: When you run the tool, a browser window will open for Microsoft authentication. Sign in with your Dataverse credentials.

2. **Solution Selection**: The tool displays a numbered list of all managed solutions in your environment:
   ```
   Available Managed Solutions:
   ----------------------------
     1. Contoso Core Solution (contoso_core) - v1.0.0.0
     2. Contoso Sales (contoso_sales) - v1.2.0.0

   Enter the number of the solution to check (or 0 to exit):
   ```

3. **Component Scanning**: After selecting a solution, the tool scans all components and checks for unmanaged layers.

4. **Layer Review**: For each component with an unmanaged layer, you'll see details like:
   ```
   ===========================================
     UNMANAGED LAYER FOUND
   ===========================================
     Component Name:  account
     Component Type:  Entity
     Solution Layer:  Active
     Layer Order:     0
     Modified On:     2024-01-15 14:30:22
     Modified By:     ContosoPublisher
   -------------------------------------------
   Do you want to remove this unmanaged layer? (y/n/a=all/s=skip all):
   ```

5. **Removal Options**:
   - `y` - Remove this specific unmanaged layer
   - `n` - Skip this layer and continue to the next
   - `a` - Automatically remove all remaining unmanaged layers
   - `s` - Skip all remaining components and finish

6. **Summary**: After processing, you'll see a summary:
   ```
   -------------------------------------------
   Summary:
     Standard components with unmanaged layers: 5
     Power Pages components with unmanaged customizations: 2
     Total unmanaged layers removed: 3
   ```

7. **Multiple Solutions**: After completing a solution, you'll be prompted:
   ```
   Do you want to check another solution? (y/n):
   ```

## Power Pages Support

Power Pages components don't use the standard `msdyn_componentlayer` table for tracking customizations. Instead, unmanaged customizations are tracked in the `powerpagecomponent` table with a reference to the "Active Solution". This tool:

1. Finds the "Active Solution" in the environment (a special system solution)
2. Queries the `powerpagecomponent` table for records where `solutionid = Active Solution ID` AND `ismanaged = true`
3. These records represent unmanaged customizations on managed Power Pages components
4. Displays component details (name, type, modified by/on)
5. Uses `RemoveActiveCustomization` API to remove the unmanaged layer

Power Pages component types detected:
- Publishing State, Web Page, Web File
- Web Link Set, Web Link, Page Template
- Content Snippet, Web Template, Site Setting
- Web Page Access Control Rule, Web Role, Website Access
- Site Marker, Basic Form, List
- Table Permission, Advanced Form, and more

## Important Notes

- **Use with caution**: Removing unmanaged layers is a destructive operation that cannot be undone. The component will revert to the state defined by the highest managed layer (or be removed if no managed layers exist).

- **Test first**: Always test in a non-production environment before running against production data.

- **Backup**: Consider exporting your solution as a backup before removing layers.

- **Permissions**: You need sufficient privileges to remove active customizations. System Administrator or System Customizer roles are recommended.

## Troubleshooting

### "Component layer entity not available"
This message appears if the `msdyn_componentlayer` entity is not available in your environment. This entity was introduced in later versions of Dataverse. Ensure your environment is up to date.

### Authentication Issues
- Ensure you're signing in with an account that has access to the target environment
- Check that pop-ups are not blocked in your browser
- Try clearing the token cache by deleting the `tokencache.dat` file

### "Error removing layer"
- Verify you have the necessary security privileges
- Some components may have dependencies that prevent layer removal
- Check if the component is part of a managed solution that's blocking the removal

## License

This project is provided as-is for educational and administrative purposes.
