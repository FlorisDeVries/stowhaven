using FlorisDeV.BackupContracts.Infrastructure;
using FlorisDeV.BackupContracts.State;

namespace FlorisDeV.BackupContracts.Application;

public class BackupRunStartResult
{
    public BackupRun Run { get; init; } = null!;
    public SasUrlInfo SasUrl { get; init; } = null!;
    public SasUrlInfo ManifestSasUrl { get; init; } = null!;
}