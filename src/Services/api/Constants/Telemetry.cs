using System.Diagnostics;

namespace FlorisDeV.BackupApi.Constants;

public class Telemetry
{
    public const string ActivitySourceName = "FlorisDeV.BackupApi";
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
}