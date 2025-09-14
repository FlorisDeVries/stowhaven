using System.Diagnostics;
using System.Reflection;
using Azure.Monitor.OpenTelemetry.Exporter;
using FlorisDeV.Logging.ApplicationInsights;
using FlorisDeV.Logging.Filtering;
using FlorisDeV.Logging.OpenTelemetry;
using Microsoft.ApplicationInsights.AspNetCore.Extensions;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Instrumentation.Http;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Enrichers.Span;
using Serilog.Events;

namespace FlorisDeV.Logging;

public static class HostBuilderExtensions
{
    /// <summary>
    ///   Adds Serilog as logging library
    /// </summary>
    /// <param name="hostBuilder"></param>
    /// <param name="applicationName"></param>
    public static void AddSerilog(this IHostBuilder hostBuilder, string applicationName)
    {
        // Bootstrap replaced by fully-configured logger once the host has loaded
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .CreateBootstrapLogger();

        // Fully-configured logger loaded from configuration
        hostBuilder.UseSerilog((context, services, configuration) =>
        {
            configuration
                .Enrich.WithSpan()
                .ReadFrom.Configuration(context.Configuration)
                .ReadFrom.Services(services)
                .Enrich.WithProperty("ApplicationName", applicationName);
        });
    }

    /// <summary>
    ///   Adds support for telemetry and logging using Application Insights.
    /// </summary>
    /// <param name="builder"></param>
    /// <remarks>The OpenTelemetry (open-source, vendor agnostic) provides better automatic instrumentation</remarks>
    public static void AddApplicationInsights(this WebApplicationBuilder builder)
    {
        var entryAssembly = Assembly.GetEntryAssembly() ?? Assembly.GetCallingAssembly();

        builder.Services
            .AddApplicationInsightsTelemetry()
            .Configure<ApplicationInsightsServiceOptions>(o => {
                //   The service options are loaded from "ApplicationInsights" section in appsettings.json.
                //   When OTel instrumentation is used, Application Insights instrumentation should be
                //   disabled in appsettings.json under ApplicationInsights config section (prefered way)
                //   - "EnableRequestTrackingTelemetryModule": false,
                //   - "EnableDependencyTrackingTelemetryModule": false
                o.DeveloperMode = builder.Environment.IsDevelopment();
                o.ApplicationVersion = FileVersionInfo.GetVersionInfo(entryAssembly.Location).ProductVersion;
            })
            .AddSingleton<ITelemetryInitializer, ContextEnrichmentTelemetryInitializer>()
            .AddApplicationInsightsTelemetryProcessor<FilteringTelemetryProcessor>();
    }

    /// <summary>
    ///    Adds and configures the OpenTelemetry SDK services.
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="activitySourceName"></param>
    /// <param name="enableAutoInstrumentation">
    ///  <para>
    ///    Collect metrics and traces about incoming web requests and outgoing HTTP requests.
    ///  </para>
    ///  <para>
    ///    If true, you'll have to disable Application Insights request and dependency tracking modules.
    ///    This can be done in appsettings.json under ApplicationInsights config section by setting
    ///    EnableRequestTrackingTelemetryModule and EnableDependencyTrackingTelemetryModule to false.
    ///  </para>
    /// </param>
    public static void AddOpenTelemetry(this WebApplicationBuilder builder,
        string activitySourceName,
        bool enableAutoInstrumentation = false)
    {
        var configuration = builder.Configuration;

        var serviceName = configuration.GetValue<string>("OTEL_SERVICE_NAME");
        var zipkinEndpointAddress = configuration.GetValue<string>("OTEL_EXPORTER_ZIPKIN_ENDPOINT");
        var azureMonitorConnection = configuration.GetValue<string>("OTEL_EXPORTER_AZURE_MONITOR_CONNECTION");

        // shared resource to use for both OTel metrics and tracing
        var appResourceBuilder = ResourceBuilder.CreateDefault()
            .AddService(serviceName ?? builder.Environment.ApplicationName);

        // configure instrumentation
        var configSection = configuration.GetSection(TelemetryFilteringOptions.SectionName);
        builder.Services
            .Configure<TelemetryFilteringOptions>(configSection)
            .Configure<AspNetCoreTraceInstrumentationOptions>(options =>
            {
                options.RecordException = true;
                options.EnrichWithHttpRequest = ActivityEnrichment.EnrichWithHttpRequest;
                options.EnrichWithHttpResponse = ActivityEnrichment.EnrichWithHttpResponse;
            })
            .Configure<HttpClientTraceInstrumentationOptions>(options =>
            {
                options.RecordException = true;
            });

        builder.Services
            .AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                // decorate our service name so we can easily find it
                tracing.SetResourceBuilder(appResourceBuilder);

                // receive traces from our own source
                tracing.AddSource(activitySourceName);

                // automatic instrumentation
                if (enableAutoInstrumentation)
                {
                    // collect metrics and traces about incoming web requests
                    tracing.AddAspNetCoreInstrumentation();

                    // collects metrics and traces about outgoing HTTP requests
                    tracing.AddHttpClientInstrumentation();
                }

                // add filtering for health endpoints
                tracing.AddProcessor<ActivityFilteringProcessor>();

                // exporters
                if (!string.IsNullOrEmpty(zipkinEndpointAddress))
                {
                    tracing.AddZipkinExporter(c => c.Endpoint = new Uri(zipkinEndpointAddress));
                }

                if (!string.IsNullOrEmpty(azureMonitorConnection))
                {
                    tracing.AddAzureMonitorTraceExporter(c => c.ConnectionString = azureMonitorConnection);
                }
            });
    }
}