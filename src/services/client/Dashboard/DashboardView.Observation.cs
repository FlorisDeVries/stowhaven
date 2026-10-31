namespace FlorisDeV.BackupClient.Dashboard;

internal static partial class DashboardView
{
    private const string ObservationJavaScript = """

        function renderObservation(data,rows) {
          rows.push(['Observation',data.observation],['Client activity',data.executionState === 'active' ? 'A backup attempt is running; duplicate starts are prevented.' : data.executionState === 'inactive' ? 'No backup attempt was running at the last check.' : 'Unavailable — current execution cannot be verified.']);
          if(data.observation === 'interrupted') status.textContent='Interrupted attempt — execution ended without a recorded final result. Completion is unknown.';
          else if(data.observation === 'stale') status.textContent='Stale observation — no heartbeat for more than 15 seconds. Silence does not establish process exit.';
          else if(data.observation === 'unavailable') status.textContent=data.message || 'Observation unavailable. Displayed details are last verified evidence; current activity and completion are unknown.';
          if(data.observation !== 'fresh') rows.push(['Evidence context','Recorded evidence only; activity and counters do not describe current work.']);
        }
        """;
}
