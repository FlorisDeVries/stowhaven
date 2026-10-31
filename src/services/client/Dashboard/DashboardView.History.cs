namespace FlorisDeV.BackupClient.Dashboard;

internal static partial class DashboardView
{
    private const string HistoryJavaScript = """
        const historyButton = document.getElementById('open-history'), historyNotice = document.getElementById('history-notice'), historyList = document.getElementById('attempts');
        historyButton.addEventListener('click', async () => {
          historyButton.disabled = true;
          try {
            const response = await fetch('/api/history', {cache:'no-store',headers:authorization()});
            historyList.replaceChildren();
            if (response.status === 401) { historyNotice.textContent='Authorization expired. Run open-dashboard again.'; return; }
            if (!response.ok) throw new Error();
            const data = await response.json();
            if (data.state !== 'available') { historyNotice.textContent=data.message || 'History unavailable.'; return; }
            historyNotice.textContent=data.attempts.length ? 'Newest first. Up to 100 attempts are retained. Open an attempt for its runs and file problems. Historical active observations have unknown final outcomes.' : 'No attempts recorded yet.';
            for (const attempt of data.attempts) {
              const item=document.createElement('details'), summary=document.createElement('summary'), values=document.createElement('dl');
              const outcome=attempt.result === 'active' ? 'Last observed active; final outcome unknown' : attempt.result;
              summary.textContent=new Date(attempt.startedAt).toLocaleString() + ' · ' + outcome + ' · ' + attempt.attemptId;
              item.append(summary,values); historyList.append(item);
              const rows=[['Targets',attempt.targetNames.join(', ')],['Files examined',attempt.filesScanned]];
              renderRuns(attempt,rows);
              for (const [name,value] of rows) { const dt=document.createElement('dt'), dd=document.createElement('dd'); dt.textContent=name; dd.textContent=value; values.append(dt,dd); }
              const diagnostics=attempt.diagnostics || {}, counts=document.createElement('p');
              counts.textContent='File problems: skipped ' + (diagnostics.skipped || 0) + ', changed ' + (diagnostics.changed || 0) + ', deferred ' + (diagnostics.deferred || 0) + ', failed ' + (diagnostics.failed || 0) + ', server rejected ' + (diagnostics.serverRejected || 0) + '.';
              item.append(counts);
              if (diagnostics.truncated) { const warning=document.createElement('p'); warning.className='notice'; warning.textContent='Diagnostic details are incomplete: up to 100 samples are retained. Category counts include every observed problem.'; item.append(warning); }
              const problems=document.createElement('ul'); item.append(problems);
              for (const entry of (diagnostics.entries || [])) { const problem=document.createElement('li'); problem.textContent=new Date(entry.at).toLocaleString() + ' · ' + entry.category + ' · ' + entry.targetName + '/' + entry.relativePath + ' · ' + entry.action; problems.append(problem); }
            }
          } catch { historyNotice.textContent='History could not be read. Check the companion and retry.'; }
          finally { historyButton.disabled=false; }
        });
        """;
}
