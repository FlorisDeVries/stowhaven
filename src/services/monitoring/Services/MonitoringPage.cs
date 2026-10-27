using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;

namespace FlorisDeV.BackupMonitoring.Services;

public abstract class MonitoringPage : ComponentBase, IDisposable
{
    [Inject] protected MonitoringApi Api { get; set; } = default!;
    [Inject] protected MonitoringOptions Options { get; set; } = default!;
    [Inject] private AuthenticationStateProvider Authentication { get; set; } = default!;
    protected CancellationTokenSource Lifetime { get; } = new();
    protected bool Busy { get; private set; }
    protected string? Error { get; private set; }
    protected DateTimeOffset? LoadedAt { get; private set; }
    protected abstract Task LoadAsync();

    protected override async Task OnParametersSetAsync()
    {
        if (Options.IsConfigured && (await Authentication.GetAuthenticationStateAsync()).User.Identity?.IsAuthenticated == true)
            await RunAsync(LoadAsync);
    }

    protected async Task RunAsync(Func<Task> operation)
    {
        if (Busy) return;
        Busy = true;
        Error = null;
        try
        {
            await operation();
            LoadedAt = DateTimeOffset.UtcNow;
        }
        catch (AccessTokenNotAvailableException exception) { exception.Redirect(); }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch (OperationCanceledException) { Error = "The request timed out. The service may be waking from idle; try refreshing."; }
        catch (MonitoringApiException exception) { Error = exception.Message; }
        catch (Exception) { Error = "Unable to load monitoring data. Check your connection and gateway configuration, then refresh."; }
        finally { Busy = false; }
    }

    public void Dispose()
    {
        Lifetime.Cancel();
        Lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
}
