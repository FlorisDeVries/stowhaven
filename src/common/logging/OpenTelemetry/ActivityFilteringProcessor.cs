using System.Diagnostics;
using FlorisDeV.Logging.Filtering;
using Microsoft.Extensions.Options;
using OpenTelemetry;

namespace FlorisDeV.Logging.OpenTelemetry;

internal sealed class ActivityFilteringProcessor : BaseProcessor<Activity>
{
    private const string AttributeHttpUrl = "http.url";
    private const string AttributeHttpTarget = "http.target";
    private const string AttributeHttpMethod = "http.method";

    private ISet<TelemetryFilteringWildcardMatcher>? _ignoreDependencies;
    private ISet<TelemetryFilteringWildcardMatcher>? _ignoreRequests;
    private ISet<string>? _ignoreOperationsNames;

    public ActivityFilteringProcessor(IOptionsMonitor<TelemetryFilteringOptions> filteringOptions)
    {
        InitOptions(filteringOptions.CurrentValue);
        filteringOptions.OnChange(InitOptions);
    }

    public override void OnEnd(Activity activity)
    {
        var ignore = IsOperationIgnored(activity) ||
                     IsDependencyIgnored(activity) ||
                     IsRequestIgnored(activity);

        if (ignore)
        {
            activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
        }
    }

    private bool IsDependencyIgnored(Activity activity)
    {
        if (activity.Kind is ActivityKind.Client && _ignoreDependencies is { Count: > 0 })
        {
            var url = activity.GetTagItem(AttributeHttpUrl) as string;
            var method = activity.GetTagItem(AttributeHttpMethod) as string;

            return _ignoreDependencies.Any(d => d.IsOperationMatch(method) && d.IsTargetMatch(url));
        }

        return false;
    }

    private bool IsRequestIgnored(Activity activity)
    {
        if (activity.Kind is ActivityKind.Server && _ignoreRequests is { Count: > 0 })
        {
            var target = activity.GetTagItem(AttributeHttpTarget) as string;
            var method = activity.GetTagItem(AttributeHttpMethod) as string;

            return _ignoreRequests.Any(d => d.IsOperationMatch(method) && d.IsTargetMatch(target));
        }

        return false;
    }

    private bool IsOperationIgnored(Activity activity)
    {
        if (_ignoreOperationsNames is { Count: > 0 })
        {
            // e.g. OperationName -> System.Net.Http.HttpRequestOut
            //      OperationName -> Microsoft.AspNetCore.Hosting.HttpRequestIn
            return _ignoreOperationsNames.Contains(activity.OperationName);
        }

        return false;
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