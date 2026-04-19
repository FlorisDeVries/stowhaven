using System.ComponentModel.DataAnnotations;

namespace FlorisDeV.BackupApi.Models.Api.Requests;

public class CommitBackupRunRequest
{
    [Required]
    public Guid DeviceId { get; set; }

    [Required]
    public Guid RunId { get; set; }

    /// <summary>
    /// Optional path to the run-manifest.json blob (e.g., "runs/{deviceId}/{runId}/run-manifest.json").
    /// If not provided, defaults to "runs/{deviceId}/{runId}/run-manifest.json".
    /// </summary>
    public string? ManifestBlobPath { get; set; }
}