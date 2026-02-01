namespace DataverseUnmanagedLayerFixer.Models;

/// <summary>
/// Represents the result of processing a component for unmanaged layer detection.
/// </summary>
public record ComponentResult(
    string ComponentName,
    string ComponentType,
    Guid ComponentId,
    string EntityName,
    string SolutionLayer,
    DateTime? ModifiedOn,
    string ModifiedBy,
    bool WasRemoved,
    string RemovalStatus
);
