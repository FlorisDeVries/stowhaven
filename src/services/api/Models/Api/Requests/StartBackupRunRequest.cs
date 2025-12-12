using System.ComponentModel.DataAnnotations;

namespace FlorisDeV.BackupApi.Models.Api.Requests;

public class StartBackupRunRequest
{
    [Required]
    public Guid DeviceId { get; set; }
}