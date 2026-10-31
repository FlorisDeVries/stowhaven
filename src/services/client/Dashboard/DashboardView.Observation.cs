namespace FlorisDeV.BackupClient.Dashboard;

internal static partial class DashboardView
{
    private const string ObservationJavaScript = """

        function renderObservation(data,rows) {
          rows.push(['Observation',data.observation],['Execution ownership',data.executionState === 'active' ? 'An execution owns the state database; duplicate starts are protected.' : data.executionState === 'inactive' ? 'No execution owned the database at the last probe.' : 'Unavailable — current execution cannot be verified.']);
          if(data.observation === 'interrupted') status.textContent='Interrupted attempt — execution ended without a recorded final result. Completion is unknown.';
          else if(data.observation === 'stale') status.textContent='Stale observation — no heartbeat for more than 15 seconds. Silence does not establish process exit.';
          else if(data.observation === 'unavailable') status.textContent=data.message || 'Observation unavailable. Displayed details are last verified evidence; current activity and completion are unknown.';
          if(data.observation !== 'fresh') rows.push(['Evidence context','Recorded evidence only; activity and counters do not describe current work.']);
        }
        """;
}
