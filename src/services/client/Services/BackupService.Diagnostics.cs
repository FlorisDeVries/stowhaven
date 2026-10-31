using FlorisDeV.BackupClient.Dashboard;
using FlorisDeV.BackupClient.Models;

namespace FlorisDeV.BackupClient.Services;

public partial class BackupService
{
    private DashboardDiagnosticCollector? _diagnostics;

    private void Diagnose(string category, TaggedFile file)
        => _diagnostics?.Add(category, file.TargetName, file.GetRelativePath());

    private void DiagnoseServerRejected(string logicalPath)
    {
        var separator = logicalPath.IndexOf('/');
        if (separator <= 0 || !_options.BackupTargets.ContainsKey(logicalPath[..separator]))
            return;
        _diagnostics?.Sample("serverRejected", logicalPath[..separator], logicalPath[(separator + 1)..]);
    }
}
