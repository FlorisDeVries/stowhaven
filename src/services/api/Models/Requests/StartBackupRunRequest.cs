using System.ComponentModel.DataAnnotations;

namespace FlorisDeV.BackupApi.Models.Requests;

public class StartBackupRunRequest
{
    [Required]
    public Guid DeviceId { get; set; }
}