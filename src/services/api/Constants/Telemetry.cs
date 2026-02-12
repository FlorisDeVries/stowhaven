using System.Diagnostics;

namespace FlorisDeV.BackupApi.Constants;

public class Telemetry
{
    public const string ActivitySourceName = "florisdev.backup.api";
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
}