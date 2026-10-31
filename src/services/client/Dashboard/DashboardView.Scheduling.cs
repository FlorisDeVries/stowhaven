namespace FlorisDeV.BackupClient.Dashboard;

internal static partial class DashboardView
{
    private const string SchedulingJavaScript = """

        function renderScheduling(schedule) {
          const message=document.getElementById('scheduling-status'), list=document.getElementById('scheduling-details');
          list.replaceChildren();
          message.textContent=schedule ? schedule.message : 'Scheduling information unavailable. Check the companion and authorize this browser with open-dashboard.';
          if(!schedule) return;
          const labels={enabled:'Enabled',disabled:'Disabled',masked:'Masked',unknown:'Unknown',missingUnit:'Missing user unit',userSessionUnavailable:'User session unavailable',schedulerUnavailable:'Scheduler information unavailable',incompatibleSchedule:'Incompatible configuration',unavailable:'Unavailable',permissionDenied:'Access denied',invalidUnit:'Invalid unit configuration',unsupported:'Unsupported platform'};
          const rows=[['Timer status',labels[schedule.state] || 'Unavailable'],['Enabled',schedule.enabled === true ? 'Yes' : schedule.enabled === false ? 'No' : 'Unknown'],['Timer activity',schedule.activeState],['Next activation',schedule.nextActivationState === 'known' && schedule.nextActivation ? new Date(schedule.nextActivation).toLocaleString() : 'Unknown — no activation time reported by the user manager']];
          for(const [name,value] of rows) { const dt=document.createElement('dt'),dd=document.createElement('dd'); dt.textContent=name; dd.textContent=value; list.append(dt,dd); }
        }
        """;
}
