using System.ComponentModel.DataAnnotations;

namespace FlorisDeV.BackupApi.Models.Api.Requests;

public class CommitBackupRunRequest
{
    [Required]
    public Guid DeviceId { get; set; }

    [Required]
    public Guid RunId { get; set; }
}