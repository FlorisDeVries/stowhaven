namespace FlorisDeV.BackupClient.Dashboard;

internal static partial class DashboardView
{
    private const string RunJavaScript = """
        function renderRuns(s,rows) {
          for (const run of (s.runs || [])) {
            rows.push(['Backup run',run.runId],['Origin attempt',run.originAttemptId || 'Unknown (legacy record)'],
              ['Attempts handling run',(run.attemptIds || []).join(', ')],['Run outcome',run.outcome],
              ['Commit',run.commitId || 'Not submitted'],['Server processing',run.serverProcessing],
              ['Local completion',run.localCompletion],['Run cleanliness',run.cleanliness],
              ['Original scan',run.scanStartedAt ? new Date(run.scanStartedAt).toLocaleString() : 'Unknown (legacy record)'],
              ['Skipped / deferred / failed',[run.filesSkipped,run.filesDeferred,run.filesFailed].map(x => x == null ? 'unknown' : x).join(' / ')]);
          }
        }
        """;
}
