using System.ComponentModel.DataAnnotations;

namespace FlorisDeV.BackupContracts.Api.Requests;

public sealed record StartRestoreRequest
{
    [Required]
    [MinLength(1)]
    public required IReadOnlyList<string> LogicalPaths { get; init; }
}
