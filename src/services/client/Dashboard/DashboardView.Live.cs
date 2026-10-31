namespace FlorisDeV.BackupClient.Dashboard;

internal static partial class DashboardView
{
    private const string LiveJavaScript = """

        function renderLive(s,data,rows) {
          const p=s.live;
          if (!p) return;
          if (p.active) {
            const activities=[];
            if(p.apiWaking) activities.push('API waking');
            if(p.scanning) activities.push('Scanning');
            if(p.uploading) activities.push('Uploading');
            status.textContent=activities.length ? activities.join(' · ') : 'Backup attempt active · waiting for remote or local work';
            if(data.stale) status.textContent='Stale observation — no heartbeat for more than 15 seconds. Backup process state is unknown.';
          }
          rows.push(['Unchanged files',p.filesUnchanged],['Uploaded files',p.filesUploaded],['Uploaded bytes',p.uploadedBytes.toLocaleString()],
            ['Skipped files',p.filesSkipped],['Deferred files',p.filesDeferred],['Failed files',p.filesFailed],
            ['Current target',p.currentTarget || 'Not observed'],['Elapsed',Math.floor(p.elapsedSeconds)+' seconds'],
            ['Average transfer rate',Math.round(p.averageBytesPerSecond).toLocaleString()+' bytes/sec · '+p.averageFilesPerSecond.toFixed(1)+' files/sec'],
            ['Scan total',p.totalFilesToScan == null ? 'Unknown — examining a streaming selection' : p.totalFilesToScan],
            ['Upload completion',p.uploadPercent == null ? 'Unknown — total transfer size unavailable' : p.uploadPercent.toFixed(1)+'%'],
            ['Time remaining',p.estimatedSecondsRemaining == null ? 'Unknown' : Math.ceil(p.estimatedSecondsRemaining)+' seconds'],
            ['Last heartbeat',new Date(p.heartbeatAt).toLocaleString()]);
          if(p.warnings.length) rows.push(['Warnings',p.warnings.join(' ')]);
        }
        """;
}
