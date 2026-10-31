namespace FlorisDeV.BackupClient.Dashboard;

internal static partial class DashboardView
{
    private const string FreshnessJavaScript = """

        function renderFreshness(s,data,rows) {
          const f=data.freshness, e=s.evidence || {};
          if(f) {
            rows.unshift(['Freshness',f.message],['Age of last clean observation',f.ageSeconds == null ? 'Unknown' : Math.floor(f.ageSeconds)+' seconds'],
              ['Maximum backup age',f.maximumAgeSeconds == null ? 'Not configured' : f.maximumAgeSeconds+' seconds']);
            if(f.state === 'configurationChanged') notice.textContent=f.message;
          }
          function clean(label,item) {
            rows.push([label,item ? (item.outcome === 'cleanCheck' ? 'Clean check; no server backup created' : 'Clean backup; server and local completion confirmed') : 'Unknown — no proven clean evidence']);
            if(item) rows.push([label+' scan started',new Date(item.scanStartedAt).toLocaleString()],
              [label+' completed',new Date(item.completedAt).toLocaleString()]);
          }
          clean('Last clean result',e.lastCleanResult);
          clean('Last clean check',e.lastCleanCheck);
          const backup=e.lastCompletedServerBackup;
          rows.push(['Last completed server backup',backup ? backup.runId+' · server '+backup.outcome+' · local '+backup.localCompletion+' · cleanliness '+backup.cleanliness : s.serverBackupEvidence === 'none' ? 'No completed server backup exists yet' : 'Unknown — historical timestamps cannot establish cleanliness']);
          if(backup) rows.push(['Server backup scan started',backup.scanStartedAt ? new Date(backup.scanStartedAt).toLocaleString() : 'Unknown (legacy evidence)'],
            ['Server completion time',backup.serverCompletedAt ? new Date(backup.serverCompletedAt).toLocaleString() : 'Not provided by server'],
            ['Server completion observed',new Date(backup.serverCompletionObservedAt).toLocaleString()],
            ['Local completion recorded',backup.completedAt ? new Date(backup.completedAt).toLocaleString() : 'Pending — successful backup completion is not established']);
          if(backup) notice.textContent=f && f.state === 'configurationChanged' ? f.message : '';
          if(!backup && s.serverBackupEvidence === 'none') notice.textContent=(f && f.state === 'configurationChanged' ? f.message+' ' : '')+'No completed server backup exists yet.';
        }
        """;
}
