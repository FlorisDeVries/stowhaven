using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlorisDeV.BackupApi.Constants;
using FlorisDeV.BackupApi.Services;
using FlorisDeV.BackupApi.Telemetry;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi.Models;

namespace FlorisDeV.BackupApi;

public static class ProgramExtensions
{
    extension(WebApplicationBuilder builder)
    {
        public void AddApplicationServices()
        {
            builder.Services.AddSingleton<TelemetryProvider>();
            builder.Services.AddScoped<IBlobStorageService, BlobStorageService>();
            builder.Services.AddScoped<ISasUrlService, SasUrlService>();
            builder.Services.AddScoped<IBackupRunService, BackupRunService>();
            builder.Services.AddScoped<IBackupProcessingService, BackupProcessingService>();
            builder.Services.AddScoped<IBackupEventPublisher, BackupEventPublisher>();
            builder.Services.AddScoped<IManifestManager, ManifestManager>();
            builder.Services.AddScoped<ISecretService, SecretService>();
            builder.Services.AddScoped<IDeviceRegistryService, DeviceRegistryService>();
            builder.Services.AddScoped<IDeviceAuthorizationService, DeviceAuthorizationService>();
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
                        : "Backup endpoints secured with JWT Bearer authentication via Azure AD"
                });

                // Add JWT Bearer security definition for non-development environments
                if (!builder.Environment.IsDevelopment())
                {
                    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
                    {
                        Description =
                            "JWT Authorization header using the Bearer scheme. Example: \"Authorization: Bearer {token}\"",
                        Name = "Authorization",
                        In = ParameterLocation.Header,
                        Type = SecuritySchemeType.Http,
                        Scheme = "bearer",
                        BearerFormat = "JWT"
                    });

                    c.AddSecurityRequirement(new OpenApiSecurityRequirement
                    {
                        {
                            new OpenApiSecurityScheme
                            {
                                Reference = new OpenApiReference
                                {
                                    Type = ReferenceType.SecurityScheme,
                                    Id = "Bearer"
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