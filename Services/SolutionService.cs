using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DataverseUnmanagedLayerFixer.Services;

/// <summary>
/// Service for solution retrieval and selection.
/// </summary>
public class SolutionService
{
    private readonly DataverseService _dataverseService;

    public SolutionService(DataverseService dataverseService)
    {
        _dataverseService = dataverseService;
    }

    /// <summary>
    /// Gets all managed solutions from the environment.
    /// </summary>
    public async Task<List<Entity>> GetManagedSolutionsAsync()
    {
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

        var result = await _dataverseService.RetrieveMultipleAsync(query);
        return result.Entities.ToList();
    }

    /// <summary>
    /// Gets the Active Solution (unmanaged customizations container).
    /// </summary>
    public async Task<Entity?> GetActiveSolutionAsync()
    {
        var query = new QueryExpression("solution")
        {
            ColumnSet = new ColumnSet("solutionid", "uniquename", "friendlyname"),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression("uniquename", ConditionOperator.Equal, "Active")
                }
            }
        };

        var result = await _dataverseService.RetrieveMultipleAsync(query);
        return result.Entities.FirstOrDefault();
    }

    /// <summary>
    /// Selects solutions interactively or by filter.
    /// </summary>
    public async Task<List<Entity>> SelectSolutionsAsync(string? solutionNameFilter = null)
    {
        Console.WriteLine("Fetching solutions...");
        var solutions = await GetManagedSolutionsAsync();

        if (solutions.Count == 0)
        {
            Console.WriteLine("No managed solutions found.");
            return new List<Entity>();
        }

        // Auto-select if filter provided
        if (!string.IsNullOrWhiteSpace(solutionNameFilter))
        {
            var selected = SelectByFilter(solutions, solutionNameFilter);
            if (selected.Count > 0)
                return selected;

            Console.WriteLine("No matching solutions found. Showing list...");
        }

        return SelectInteractively(solutions);
    }

    private List<Entity> SelectByFilter(List<Entity> solutions, string filter)
    {
        var filterNames = filter.Split(',').Select(s => s.Trim()).ToList();
        var matchingSolutions = new List<Entity>();

        foreach (var filterName in filterNames)
        {
            var match = solutions.FirstOrDefault(s =>
            {
                var friendlyName = s.GetAttributeValue<string>("friendlyname") ?? "";
                var uniqueName = s.GetAttributeValue<string>("uniquename") ?? "";
                return friendlyName.Equals(filterName, StringComparison.OrdinalIgnoreCase) ||
                       uniqueName.Equals(filterName, StringComparison.OrdinalIgnoreCase);
            });

            if (match != null)
            {
                var name = match.GetAttributeValue<string>("friendlyname") ?? "Unknown";
                Console.WriteLine($"Auto-selected solution: {name}");
                matchingSolutions.Add(match);
            }
            else
            {
                Console.WriteLine($"Solution '{filterName}' not found.");
            }
        }

        return matchingSolutions;
    }

    private List<Entity> SelectInteractively(List<Entity> solutions)
    {
        Console.WriteLine();
        Console.WriteLine("Available Managed Solutions:");
        Console.WriteLine("----------------------------");

        for (int i = 0; i < solutions.Count; i++)
        {
            var sol = solutions[i];
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

            var selectedSolutions = ParseSolutionSelection(solutions, input);
            if (selectedSolutions != null)
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

    private List<Entity>? ParseSolutionSelection(List<Entity> solutions, string input)
    {
        var selectedSolutions = new List<Entity>();
        var parts = input.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s));
        bool validInput = true;

        foreach (var part in parts)
        {
            if (int.TryParse(part, out int selection))
            {
                if (selection >= 1 && selection <= solutions.Count)
                {
                    var selectedSolution = solutions[selection - 1];
                    if (!selectedSolutions.Contains(selectedSolution))
                    {
                        selectedSolutions.Add(selectedSolution);
                    }
                }
                else
                {
                    Console.WriteLine($"  Invalid selection: {selection} (must be 1-{solutions.Count})");
                    validInput = false;
                }
            }
            else
            {
                Console.WriteLine($"  Invalid input: '{part}' is not a number");
                validInput = false;
            }
        }

        return validInput && selectedSolutions.Count > 0 ? selectedSolutions : null;
    }
}
