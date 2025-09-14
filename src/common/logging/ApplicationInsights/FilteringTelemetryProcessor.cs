using FlorisDeV.Logging.Filtering;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Options;

namespace FlorisDeV.Logging.ApplicationInsights;

/// <summary>
///    Filters out "noise" in Application Insights telemetry
/// </summary>
public class FilteringTelemetryProcessor : ITelemetryProcessor
{
    private ISet<TelemetryFilteringWildcardMatcher>? _ignoreDependencies;
    private ISet<TelemetryFilteringWildcardMatcher>? _ignoreRequests;
    private ISet<string>? _ignoreOperationsNames;

    private readonly ITelemetryProcessor _next;

    public FilteringTelemetryProcessor(ITelemetryProcessor next,
        IOptionsMonitor<TelemetryFilteringOptions> filteringOptions)
    {
        _next = next;
        InitOptions(filteringOptions.CurrentValue);
        filteringOptions.OnChange(InitOptions);
    }

    public void Process(ITelemetry item)
    {
        switch (item)
        {
            case { Context.Operation.Name: { } operationName } when IsOperationIgnored(operationName):
                return;

            case RequestTelemetry request when IsRequestIgnored(request):
                return;

            case DependencyTelemetry dependency when IsDependencyIgnored(dependency):
                return;

            default:
                _next.Process(item);
                break;
        }
    }

    private bool IsOperationIgnored(string operationName)
    {
        // e.g. OPTIONS /
        return _ignoreOperationsNames?.Any(o => operationName.StartsWith(o, StringComparison.OrdinalIgnoreCase)) is true;
    }

    private bool IsRequestIgnored(RequestTelemetry request)
    {
        return _ignoreRequests?.Any(r => r.IsTargetMatch(request.Url.AbsolutePath)) is true;
    }

    private bool IsDependencyIgnored(DependencyTelemetry dependency)
    {
        return _ignoreDependencies?.Any(d => d.IsTargetMatch(dependency.Data)) is true;
    }

    private void InitOptions(TelemetryFilteringOptions currentValue)
    {
        _ignoreDependencies = currentValue.IgnoreDependencies?
            .Select(TelemetryFilteringWildcardMatcher.Create)
            .ToHashSet();

        _ignoreRequests = currentValue.IgnoreRequests?
            .Select(TelemetryFilteringWildcardMatcher.Create)
            .ToHashSet();

        _ignoreOperationsNames = currentValue.IgnoreOperationNames?
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}