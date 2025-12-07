using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlorisDeV.BackupApi.Constants;
using FlorisDeV.BackupApi.Services;
using FlorisDeV.Logging;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi.Models;
using Refit;

namespace FlorisDeV.BackupApi;

public static class ProgramExtensions
{
    public static void AddApplicationServices(this WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<ISasUrlService, SasUrlService>();
    }

    public static void AddCustomDaprClient(this WebApplicationBuilder builder)
    {
        builder.Services.AddDaprClient(configure =>
        {
            configure.UseJsonSerializationOptions(new JsonSerializerOptions
            {
                Converters =
                {
                    new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
                }
            });
        });
    }

    public static void AddCustomLogging(this WebApplicationBuilder builder)
    {
        builder.Host.AddSerilog(builder.Environment.ApplicationName);
        builder.Services.AddHttpLogging(logging => logging.LoggingFields = HttpLoggingFields.All);
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
    }

    public static void AddCustomSwagger(this WebApplicationBuilder builder, Assembly assembly)
    {
        builder.Services.AddSwaggerGen(c =>
        {
            var productVersion = FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion;

            c.SwaggerDoc("main", new OpenApiInfo
            {
                Title = builder.Environment.ApplicationName,
                Version = productVersion,
                Description = "Backup endpoints secured with api key authentication"
            });

            // make all params camelCased
            c.DescribeAllParametersInCamelCase();
            c.UseAllOfToExtendReferenceSchemas();

            // Use documentation from code
            foreach (var xmlFile in
                     Directory.GetFiles(AppContext.BaseDirectory, "*.xml", SearchOption.TopDirectoryOnly))
            {
                c.IncludeXmlComments(xmlFile, includeControllerXmlComments: true);
            }
        });
    }

    public static void AddCustomCache(this WebApplicationBuilder builder)
    {
        builder.Services.AddMemoryCache();
        builder.Services.AddDistributedMemoryCache(); // or AddDistributedRedisCache
    }

    public static void AddCustomRateLimitPolicies(this WebApplicationBuilder builder)
    {
        builder.Services.AddRateLimiter(o =>
        {
            o.AddSlidingWindowLimiter(RateLimitPolicies.ExternalHealthCheckPolicy, options =>
            {
                options.PermitLimit = 6;
                options.Window = TimeSpan.FromMinutes(1);
                options.SegmentsPerWindow = 6;
                options.QueueLimit = 1;
            });
        });
    }

    public static void ConfigureRouting(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<RouteOptions>(options => { options.LowercaseUrls = true; });
    }

    public static void ConfigureWebServer(this WebApplicationBuilder builder)
    {
        builder.WebHost.UseKestrel(options =>
        {
            options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(1);
            options.AddServerHeader = false;
        });
    }

    public static void ConfigureProxyForwarding(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.All;
            options.KnownNetworks.Clear();
            options.KnownProxies.Clear();
        });
    }

    public static void AddCustomDaprIntegration(this IMvcBuilder builder)
    {
        builder
            .AddDapr()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
                options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
            });
    }

    public static void UseCustomSwagger(this IApplicationBuilder builder, string applicationName)
    {
        builder.UseSwagger(c =>
        {
            c.PreSerializeFilters.Add((swagger, httpReq) =>
            {
                if (httpReq.Headers.TryGetValue("X-Forwarded-Prefix", out var pathBase))
                {
                    var serverUrl = $"{httpReq.Scheme}://{httpReq.Host}{pathBase}";
                    swagger.Servers = new List<OpenApiServer> { new() { Url = serverUrl } };
                }
            });
        });

        builder.UseSwaggerUI(c => { c.SwaggerEndpoint("main/swagger.json", applicationName); });
    }
}