using System.ComponentModel.DataAnnotations;

namespace FlorisDeV.BackupContracts.Api.Requests;

public class CommitBackupRunRequest
{
    [Required]
    public Guid RunId { get; set; }
}