using System.Diagnostics;
using System.Reflection;
using Azure.Monitor.OpenTelemetry.Exporter;
using FlorisDeV.Logging.ApplicationInsights;
using FlorisDeV.Logging.Filtering;
using FlorisDeV.Logging.OpenTelemetry;
using Microsoft.ApplicationInsights.AspNetCore.Extensions;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Instrumentation.Http;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Refit;
using Serilog;
using Serilog.Enrichers.Span;
using Serilog.Events;

namespace FlorisDeV.Logging;

public static class HostBuilderExtensions
{
    public static void AddCustomLogging(this WebApplicationBuilder builder)
    {
        builder.Host.AddSerilog(builder.Environment.ApplicationName);

        // Configure log sampling options
        builder.Services.Configure<LogSamplingOptions>(
            builder.Configuration.GetSection(LogSamplingOptions.SectionName));

        builder.Services.AddHttpLogging(o =>
        {
            o.LoggingFields =
                HttpLoggingFields.RequestMethod |
                HttpLoggingFields.RequestPath |
                HttpLoggingFields.RequestQuery |
                HttpLoggingFields.ResponseStatusCode |
                HttpLoggingFields.Duration |
                HttpLoggingFields.RequestHeaders |
                HttpLoggingFields.ResponseHeaders;

            // Only log harmless headers you need
            o.RequestHeaders.Add("User-Agent");
            o.RequestHeaders.Add("X-Request-Id");
            o.RequestHeaders.Add("Traceparent");

            o.ResponseHeaders.Add("X-Request-Id");
            o.ResponseHeaders.Add("Traceparent");
        });

        builder.Services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = context =>
            {
                var contextFeature = context.HttpContext.Features.Get<IExceptionHandlerFeature>();
                var exception = contextFeature?.Error;

                if (exception == null)
                {
                    return;
                }

                var problemDetails = context.ProblemDetails;
                var response = context.HttpContext.Response;

                problemDetails.Title = exception.GetType().Name;
                problemDetails.Detail = exception.Message;

                if (exception is not ApiException apiException)
                {
                    return;
                }

                // set the response status and problem details codes
                // equal to the one returned by the client api exception
                problemDetails.Status = (int)apiException.StatusCode;
                response.StatusCode = (int)apiException.StatusCode;
            };
        });

        // Note: Exception handling is done via GlobalExceptionFilter for better control.
    }

    /// <summary>
    ///   Adds Serilog as logging library
    /// </summary>
    /// <param name="hostBuilder"></param>
    /// <param name="applicationName"></param>
    public static IHostBuilder AddSerilog(this IHostBuilder hostBuilder, string applicationName)
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
            // Get sampling options
            var samplingOptions = new LogSamplingOptions();
            context.Configuration.GetSection(LogSamplingOptions.SectionName)
                .Bind(samplingOptions);

            var samplingFilter = new LogSamplingFilter(samplingOptions);

            // Get OTLP endpoint for log export
            var otlpEndpoint = context.Configuration.GetValue<string>("OTEL_EXPORTER_OTLP_ENDPOINT");
            var serviceName = context.Configuration.GetValue<string>("OTEL_SERVICE_NAME") ?? applicationName;

            var loggerConfig = configuration
                .Enrich.WithSpan()
                .Enrich.FromLogContext()
                .Enrich.WithMachineName()
                .Enrich.WithProcessId()
                .Filter.ByIncludingOnly(samplingFilter.IsEnabled)
                .ReadFrom.Configuration(context.Configuration)
                .ReadFrom.Services(services)
                .Enrich.WithProperty("ApplicationName", applicationName)
                .Enrich.WithProperty("Environment", context.HostingEnvironment.EnvironmentName);

            // Add OpenTelemetry sink if OTLP endpoint is configured
            if (!string.IsNullOrEmpty(otlpEndpoint))
            {
                loggerConfig.WriteTo.OpenTelemetry(options =>
                {
                    options.Endpoint = otlpEndpoint;
                    options.Protocol = Serilog.Sinks.OpenTelemetry.OtlpProtocol.Grpc;
                    options.ResourceAttributes = new Dictionary<string, object>
                    {
                        ["service.name"] = serviceName
                    };
                });
            }
        });

        return hostBuilder;
    }

    /// <summary>
    ///   Adds support for telemetry and logging using Application Insights.
    /// </summary>
    /// <param name="builder"></param>
    public static void AddApplicationInsights(this WebApplicationBuilder builder)
    {
        var entryAssembly = Assembly.GetEntryAssembly() ?? Assembly.GetCallingAssembly();

        builder.Services
            .AddApplicationInsightsTelemetry()
            .Configure<ApplicationInsightsServiceOptions>(o =>
            {
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
        var otlpEndpoint = configuration.GetValue<string>("OTEL_EXPORTER_OTLP_ENDPOINT");
        var azureMonitorConnection = configuration.GetValue<string>("OTEL_EXPORTER_AZURE_MONITOR_CONNECTION");

        // shared resource to use for both OTel metrics and tracing
        var resourceBuilder = ResourceBuilder.CreateDefault()
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
            .Configure<HttpClientTraceInstrumentationOptions>(options => { options.RecordException = true; });

        builder.Services
            .AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                // decorate our service name so we can easily find it
                tracing.SetResourceBuilder(resourceBuilder);

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
                if (!string.IsNullOrEmpty(otlpEndpoint))
                {
                    tracing.AddOtlpExporter(exportOptions => { exportOptions.Endpoint = new Uri(otlpEndpoint); });
                }

                if (!string.IsNullOrEmpty(azureMonitorConnection))
                {
                    tracing.AddAzureMonitorTraceExporter(c => c.ConnectionString = azureMonitorConnection);
                }
            })
            .WithMetrics(metrics =>
            {
                // decorate our service name so we can easily find it
                metrics.SetResourceBuilder(resourceBuilder);

                // receive metrics from our own meter
                metrics.AddMeter(activitySourceName);

                // automatic instrumentation
                if (enableAutoInstrumentation)
                {
                    metrics.AddAspNetCoreInstrumentation();
                    metrics.AddHttpClientInstrumentation();
                }

                // exporters
                if (!string.IsNullOrEmpty(otlpEndpoint))
                {
                    metrics.AddOtlpExporter(exportOptions => { exportOptions.Endpoint = new Uri(otlpEndpoint); });
                }

                if (!string.IsNullOrEmpty(azureMonitorConnection))
                {
                    metrics.AddAzureMonitorMetricExporter(c => c.ConnectionString = azureMonitorConnection);
                }
            });

        builder.Services.AddLogging(logging =>
        {
            logging.AddOpenTelemetry(options =>
            {
                options.SetResourceBuilder(resourceBuilder);
                options.IncludeFormattedMessage = true;
                options.IncludeScopes = true;

                if (!string.IsNullOrEmpty(otlpEndpoint))
                {
                    options.AddOtlpExporter(exportOptions => { exportOptions.Endpoint = new Uri(otlpEndpoint); });
                }

                if (!string.IsNullOrEmpty(azureMonitorConnection))
                {
                    options.AddAzureMonitorLogExporter(exportOptions =>
                        exportOptions.ConnectionString = azureMonitorConnection);
                }
            });
        });
    }

    /// <summary>
    ///   Adds and configures the OpenTelemetry SDK services for console applications.
    /// </summary>
    /// <param name="hostBuilder">The host builder to configure.</param>
    /// <param name="resourceAttributesFactory">Factory function to create resource attributes from host context.</param>
    /// <param name="activitySourceName">The name of the ActivitySource to listen to.</param>
    /// <param name="meterName">The name of the Meter to collect metrics from. Typically the same as activitySourceName.</param>
    /// <param name="enableHttpClientInstrumentation">Enable automatic HTTP client instrumentation.</param>
    /// <returns>The configured IHostBuilder for chaining.</returns>
    public static IHostBuilder AddOpenTelemetry(this IHostBuilder hostBuilder,
        Func<HostBuilderContext, OtelResourceAttributes> resourceAttributesFactory,
        string activitySourceName,
        string meterName,
        bool enableHttpClientInstrumentation = true)
    {
        hostBuilder
            .ConfigureServices((context, services) =>
            {
                var configuration = context.Configuration;
                var otlpEndpoint = configuration.GetValue<string>("OTEL_EXPORTER_OTLP_ENDPOINT");
                var azureMonitorConnection = configuration.GetValue<string>("OTEL_EXPORTER_AZURE_MONITOR_CONNECTION");

                // Build resource from factory
                var resourceAttributes = resourceAttributesFactory(context);
                var resourceBuilder = ResourceBuilder.CreateDefault()
                    .AddService(
                        serviceName: resourceAttributes.ServiceName,
                        serviceVersion: resourceAttributes.ServiceVersion);

                if (!string.IsNullOrEmpty(resourceAttributes.DeploymentEnvironment))
                {
                    resourceBuilder.AddAttributes(new Dictionary<string, object>
                    {
                        ["deployment.environment"] = resourceAttributes.DeploymentEnvironment
                    });
                }

                if (resourceAttributes.AdditionalAttributes is { Count: > 0 })
                {
                    resourceBuilder.AddAttributes(resourceAttributes.AdditionalAttributes);
                }

                // Configure OpenTelemetry tracing and logging
                services
                    .AddOpenTelemetry()
                    .WithTracing(tracing =>
                    {
                        tracing
                            .SetResourceBuilder(resourceBuilder)
                            .AddSource(activitySourceName);

                        // Add HTTP client instrumentation if enabled
                        if (enableHttpClientInstrumentation)
                        {
                            tracing.AddHttpClientInstrumentation(options => { options.RecordException = true; });
                        }

                        // Add exporters if configured
                        if (!string.IsNullOrEmpty(otlpEndpoint))
                        {
                            tracing.AddOtlpExporter(exportOptions =>
                            {
                                exportOptions.Endpoint = new Uri(otlpEndpoint);
                            });
                        }

                        if (!string.IsNullOrEmpty(azureMonitorConnection))
                        {
                            tracing.AddAzureMonitorTraceExporter(exportOptions =>
                                exportOptions.ConnectionString = azureMonitorConnection);
                        }
                    })
                    .WithMetrics(metrics =>
                    {
                        metrics.SetResourceBuilder(resourceBuilder);

                        metrics.AddMeter(meterName);

                        if (!string.IsNullOrEmpty(otlpEndpoint))
                        {
                            metrics.AddOtlpExporter(exportOptions =>
                            {
                                exportOptions.Endpoint = new Uri(otlpEndpoint);
                            });
                        }

                        if (!string.IsNullOrEmpty(azureMonitorConnection))
                        {
                            metrics.AddAzureMonitorMetricExporter(exportOptions =>
                                exportOptions.ConnectionString = azureMonitorConnection);
                        }
                    });

                services.AddLogging(logging =>
                {
                    logging.AddOpenTelemetry(options =>
                    {
                        options.SetResourceBuilder(resourceBuilder);
                        options.IncludeFormattedMessage = true;
                        options.IncludeScopes = true;

                        if (!string.IsNullOrEmpty(otlpEndpoint))
                        {
                            options.AddOtlpExporter(exportOptions =>
                            {
                                exportOptions.Endpoint = new Uri(otlpEndpoint);
                            });
                        }

                        if (!string.IsNullOrEmpty(azureMonitorConnection))
                        {
                            options.AddAzureMonitorLogExporter(exportOptions =>
                                exportOptions.ConnectionString = azureMonitorConnection);
                        }
                    });
                });

                // Set a shutdown timeout to allow telemetry to flush on application exit
                services.Configure<HostOptions>(opts => opts.ShutdownTimeout = TimeSpan.FromSeconds(10));
            });

        return hostBuilder;
    }
}