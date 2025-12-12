using FlorisDeV.BackupApi.Models.Infrastructure;
using FlorisDeV.BackupApi.Models.State;

namespace FlorisDeV.BackupApi.Models.Application;

public class BackupRunStartResult
{
    public BackupRun Run { get; set;  }
    public SasUrlInfo SasUrl { get; set; }
}