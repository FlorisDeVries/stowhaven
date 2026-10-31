namespace FlorisDeV.BackupClient.Dashboard;

internal static partial class DashboardView
{
    internal const string Html = """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Stowhaven local dashboard</title><link rel="stylesheet" href="/dashboard.css"><script defer src="/dashboard.js"></script></head>
        <body><main><p class="eyebrow">STOWHAVEN · LOCAL CLIENT</p><h1>Backup observations</h1>
        <p id="status" role="status">Connecting to your local companion…</p><p id="notice" class="notice"></p>
        <button id="start" type="button">Start backup</button><p id="start-result" role="status"></p>
        <dl id="details"></dl><p class="footnote">A clean check examines the selected files. It does not create a server backup or test restoration.</p></main></body></html>
        """;
    internal const string Css = """
        :root{font:17px/1.6 system-ui,sans-serif;color:#e7edf0;background:#102128;color-scheme:dark}body{margin:0;padding:6vh 6vw}main{max-width:850px;margin:auto}h1{font-size:clamp(2rem,5vw,3.4rem);line-height:1.15;margin:1rem 0 2rem}.eyebrow{letter-spacing:.15em;font-size:.75rem;color:#9bd8c7}.notice{font-weight:700;color:#f0d29c}.notice:empty{display:none}dl{display:grid;grid-template-columns:minmax(130px,1fr) 2fr;gap:.7rem 2rem;padding:1.5rem;background:#19333b;border-radius:12px}dl:empty{display:none}dt{color:#a5bec4}dd{margin:0;overflow-wrap:anywhere}.footnote{color:#a5bec4;font-size:.85rem;margin-top:2rem}
        """;
    internal const string JavaScript = """
        'use strict';
        const status = document.getElementById('status'), notice = document.getElementById('notice'), details = document.getElementById('details');
        function authorization() { return {'Authorization':'Bearer ' + (sessionStorage.getItem('stowhaven-session') || '')}; }
        async function poll() {
          try {
            const response = await fetch('/api/status', {cache:'no-store',headers:authorization()});
            if (response.status === 401) { status.textContent = 'Authorization expired. Run open-dashboard to authorize this browser.'; notice.textContent=''; details.replaceChildren(); return; }
            if (!response.ok) throw new Error();
            const data = await response.json();
            details.replaceChildren(); notice.textContent = '';
            if (!data.snapshot) { status.textContent = data.message || 'Reporting unavailable.'; return; }
            const s = data.snapshot;
            status.textContent = 'Last recorded attempt: ' + (({cleanCheck:'Clean check — no changes to back up.',noChanges:'No changes to back up.',pending:'Pending server completion; the next backup attempt will reconcile this work.',succeeded:'Backup succeeded: server and local completion confirmed.',completedWithErrors:'Backup completed with errors.',failed:'Backup attempt failed.'})[s.result] || 'Active at the recorded observation.');
            if (s.serverBackupEvidence === 'none') notice.textContent = 'No backup has been created yet.';
            else if (s.serverBackupEvidence === 'unknown') notice.textContent = 'Completed server backup history is unknown.';
            const rows = [['Attempt',s.attemptId],['Targets',s.targetNames.join(', ')],['Files examined',s.filesScanned],['Scan started',s.scanStartedAt ? new Date(s.scanStartedAt).toLocaleString() : 'Not observed'],['Recorded',new Date(s.observedAt).toLocaleString()],['Server backup',s.serverBackupEvidence === 'recorded' ? 'Previously recorded by the client; cleanliness is unknown.' : s.serverBackupEvidence]];
            renderRuns(s,rows);
            renderLive(s,data,rows);
            renderObservation(data,rows);
            for (const [name,value] of rows) { const dt=document.createElement('dt'), dd=document.createElement('dd'); dt.textContent=name; dd.textContent=value; details.append(dt,dd); }
          } catch { status.textContent = 'Companion unavailable. Displayed details are the last verified observation; current activity and completion are unknown.'; notice.textContent=''; }
          finally { window.setTimeout(poll,1000); }
        }
        const startButton = document.getElementById('start'), startResult = document.getElementById('start-result');
        startButton.addEventListener('click', async () => {
          startButton.disabled = true; startResult.textContent = 'Requesting backup execution…';
          try {
            const response = await fetch('/api/start', {method:'POST',headers:authorization()});
            startResult.textContent = response.status === 401 ? 'Authorization expired. Run open-dashboard again.' : (await response.json()).message;
          } catch { startResult.textContent = 'Start could not be confirmed. Check the companion and backup user service before retrying.'; }
          finally { startButton.disabled = false; }
        });
        let lastActivity = 0;
        function recordActivity() {
          if (Date.now() - lastActivity < 60000) return;
          lastActivity = Date.now();
          fetch('/api/activity',{method:'POST',headers:authorization()}).catch(() => {});
        }
        window.addEventListener('pointerdown',recordActivity);
        window.addEventListener('keydown',recordActivity);
        async function connect() {
          const ticket = location.hash.slice(1); history.replaceState(null,'',location.pathname);
          if (ticket) {
            try {
              const response = await fetch('/api/session',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({ticket})});
              if (response.ok) sessionStorage.setItem('stowhaven-session',(await response.json()).session);
            }
            catch { status.textContent='Could not authorize. Run open-dashboard again.'; }
          }
          await poll();
        }
        connect();
        """ + RunJavaScript + LiveJavaScript + ObservationJavaScript;
}
