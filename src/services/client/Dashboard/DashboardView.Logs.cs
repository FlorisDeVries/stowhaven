namespace FlorisDeV.BackupClient.Dashboard;

internal static partial class DashboardView
{
    internal const string LogsCss = """
        #logs{white-space:pre-wrap;overflow-wrap:anywhere;max-height:34rem;overflow:auto;background:#19333b;padding:1rem;border-radius:12px}button{font:inherit;padding:.5rem 1rem;cursor:pointer}
        """;
    internal const string LogsJavaScript = """
        const logButton=document.getElementById('open-logs'), logNotice=document.getElementById('logs-notice'), logText=document.getElementById('logs');
        function clearRecentLogs() { logText.textContent=''; logText.hidden=true; logNotice.textContent=''; }
        logButton.addEventListener('click',async () => {
          logButton.disabled=true; logText.textContent=''; logText.hidden=true; logNotice.textContent='Opening recent logs…';
          try {
            const response=await fetch('/api/logs',{cache:'no-store',headers:authorization()});
            if(response.status===401) { logNotice.textContent='Authorization expired. Run open-dashboard, then open recent logs again.'; return; }
            if(!response.ok) throw new Error();
            const data=await response.json();
            logNotice.textContent=data.message+(data.truncated ? ' Older or incomplete entries are omitted to keep this view bounded.' : '');
            logText.textContent=data.entries.join('\n'); logText.hidden=data.entries.length===0;
            logButton.textContent='Refresh recent logs';
          } catch { logNotice.textContent='Recent logs unavailable. Check the companion and try again.'; }
          finally { logButton.disabled=false; }
        });
        """;
}
