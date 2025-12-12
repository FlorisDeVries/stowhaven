using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlorisDeV.BackupApi.Authentication;
using FlorisDeV.BackupApi.Constants;
using FlorisDeV.BackupApi.Filters;
using FlorisDeV.BackupApi.Services;
using FlorisDeV.Logging;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi.Models;
using Refit;

namespace FlorisDeV.BackupApi;

public static class ProgramExtensions
{
    extension(WebApplicationBuilder builder)
    {
        public void AddApplicationServices()
        {
            builder.Services.AddScoped<ISasUrlService, SasUrlService>();
            builder.Services.AddScoped<IBackupRunService, BackupRunService>();
            builder.Services.AddScoped<IManifestManager, ManifestManager>();
        }

        public void AddCustomDaprClient()
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

        public void AddCustomLogging()
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
            
            // Note: Exception handling is now done via GlobalExceptionFilter for better control
        }

        public void AddCustomSwagger(Assembly assembly)
        {
            builder.Services.AddSwaggerGen(c =>
            {
                var productVersion = FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion;

                c.SwaggerDoc("main", new OpenApiInfo
                {
                    Title = builder.Environment.ApplicationName,
                    Version = productVersion,
                    Description = builder.Environment.IsDevelopment() 
                        ? "Backup endpoints (Development mode - no authentication required)"
                        : "Backup endpoints secured with api key authentication"
                });

                // Add API Key authentication for non-development environments
                if (!builder.Environment.IsDevelopment())
                {
                    c.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
                    {
                        Description = "API Key authentication. Pass your API key in the X-API-Key header.",
                        Name = "X-API-Key",
                        In = ParameterLocation.Header,
                        Type = SecuritySchemeType.ApiKey,
                        Scheme = "ApiKey"
                    });

                    c.AddSecurityRequirement(new OpenApiSecurityRequirement
                    {
                        {
                            new OpenApiSecurityScheme
                            {
                                Reference = new OpenApiReference
                                {
                                    Type = ReferenceType.SecurityScheme,
                                    Id = "ApiKey"
                                }
                            },
                            Array.Empty<string>()
                        }
                    });
                }

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

        public void AddCustomAuthentication()
        {
            if (builder.Environment.IsDevelopment())
            {
                // For local development: allow anonymous access
                builder.Services
                    .AddAuthentication("AllowAnonymous")
                    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, AllowAnonymousAuthenticationHandler>(
                        "AllowAnonymous", 
                        options => { });
            }
            else
            {
                // For production: configure your actual authentication scheme
                // Example: API Key authentication (you can replace this with Azure AD, JWT, etc.)
                builder.Services
                    .AddAuthentication("ApiKey")
                    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                        "ApiKey", 
                        options => { });
            }

            builder.Services.AddAuthorization();
        }

        public void AddCustomCache()
        {
            builder.Services.AddMemoryCache();
            builder.Services.AddDistributedMemoryCache(); // or AddDistributedRedisCache
        }

        public void AddCustomRateLimitPolicies()
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

        public void ConfigureRouting()
        {
            builder.Services.Configure<RouteOptions>(options => { options.LowercaseUrls = true; });
        }

        public void ConfigureWebServer()
        {
            builder.WebHost.UseKestrel(options =>
            {
                options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(1);
                options.AddServerHeader = false;
            });
        }

        public void ConfigureProxyForwarding()
        {
            builder.Services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.All;
                options.KnownIPNetworks.Clear();
                options.KnownProxies.Clear();
            });
        }
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