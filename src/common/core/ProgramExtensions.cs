using System.Diagnostics;
using System.Net;
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
            builder.Services.AddScoped<IManifestManager, ManifestManager>();
            builder.Services.AddScoped<ISecretService, SecretService>();
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
            builder.Services.AddHttpClient();

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
                var section = builder.Configuration.GetSection("ReverseProxy:ForwardedHeaders");

                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                                           | ForwardedHeaders.XForwardedProto
                                           | ForwardedHeaders.XForwardedHost;
                options.ForwardLimit = section.GetValue<int?>("ForwardLimit") ?? 1;

                foreach (var proxy in section.GetSection("KnownProxies").Get<string[]>() ?? [])
                {
                    options.KnownProxies.Add(ParseIpAddress(proxy, "known proxy"));
                }

                foreach (var network in section.GetSection("KnownNetworks").Get<string[]>() ?? [])
                {
                    options.KnownIPNetworks.Add(ParseIpNetwork(network));
                }
            });
        }
    }

    private static IPAddress ParseIpAddress(string value, string description)
    {
        if (IPAddress.TryParse(value, out var address))
        {
            return address;
        }

        throw new InvalidOperationException($"Invalid reverse proxy {description}: '{value}'.");
    }

    private static System.Net.IPNetwork ParseIpNetwork(string value)
    {
        if (System.Net.IPNetwork.TryParse(value, out var network))
        {
            return network;
        }

        throw new InvalidOperationException($"Invalid reverse proxy known network CIDR: '{value}'.");
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
        var configuration = builder.ApplicationServices.GetRequiredService<IConfiguration>();
        var requiredGatewayHeaderName = configuration["Swagger:RequiredGatewayHeaderName"];
        var requiredGatewayHeaderValue = configuration["Swagger:RequiredGatewayHeaderValue"];

        if (!string.IsNullOrWhiteSpace(requiredGatewayHeaderName) &&
            !string.IsNullOrWhiteSpace(requiredGatewayHeaderValue))
        {
            builder.UseWhen(
                context => context.Request.Path.StartsWithSegments("/swagger"),
                branch => branch.Use(async (context, next) =>
                {
                    if (!context.Request.Headers.TryGetValue(requiredGatewayHeaderName, out var headerValue) ||
                        !StringComparer.Ordinal.Equals(headerValue.ToString(), requiredGatewayHeaderValue))
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        return;
                    }

                    await next(context).ConfigureAwait(false);
                }));
        }

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

        builder.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("main/swagger.json", applicationName);

            var workerBaseUrl = configuration["Swagger:WorkerBaseUrl"];

            if (!string.IsNullOrWhiteSpace(workerBaseUrl))
            {
                var workerProxyPath = NormalizeProxyPath(configuration["Swagger:WorkerProxyPath"] ?? "/worker");
                c.SwaggerEndpoint($"{workerProxyPath}/swagger/main/swagger.json", "FlorisDeV.BackupWorker");
            }
        });
    }

    public static void MapWorkerSwaggerProxy(this WebApplication app)
    {
        var configuration = app.Services.GetRequiredService<IConfiguration>();
        var workerBaseUrl = configuration["Swagger:WorkerBaseUrl"];

        if (string.IsNullOrWhiteSpace(workerBaseUrl))
        {
            return;
        }

        var workerProxyPath = NormalizeProxyPath(configuration["Swagger:WorkerProxyPath"] ?? "/worker");
        var workerBaseUri = new Uri(workerBaseUrl, UriKind.Absolute);

        app.Map($"{workerProxyPath}/{{**path}}", async context =>
        {
            var httpClientFactory = context.RequestServices.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient();
            var targetUri = BuildProxyUri(workerBaseUri, context.Request.RouteValues["path"] as string, context.Request.QueryString);

            using var requestMessage = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUri);
            requestMessage.Headers.Host = context.Request.Host.Value;
            requestMessage.Headers.TryAddWithoutValidation("X-Forwarded-Proto", context.Request.Scheme);
            requestMessage.Headers.TryAddWithoutValidation("X-Forwarded-Host", context.Request.Host.Value);
            requestMessage.Headers.TryAddWithoutValidation("X-Forwarded-Prefix", workerProxyPath);

            if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
            {
                requestMessage.Content = new StreamContent(context.Request.Body);
            }

            foreach (var header in context.Request.Headers)
            {
                if (string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                {
                    requestMessage.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                }
            }

            using var responseMessage = await httpClient.SendAsync(
                requestMessage,
                HttpCompletionOption.ResponseHeadersRead,
                context.RequestAborted).ConfigureAwait(false);

            context.Response.StatusCode = (int)responseMessage.StatusCode;

            foreach (var header in responseMessage.Headers)
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }

            foreach (var header in responseMessage.Content.Headers)
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }

            context.Response.Headers.Remove("transfer-encoding");

            await responseMessage.Content.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
        }).ExcludeFromDescription();
    }

    private static Uri BuildProxyUri(Uri baseUri, string? path, QueryString queryString)
    {
        var basePath = baseUri.AbsolutePath.TrimEnd('/');
        var targetPath = path?.TrimStart('/') ?? string.Empty;
        var combinedPath = string.IsNullOrEmpty(basePath) ? $"/{targetPath}" : $"{basePath}/{targetPath}";

        return new UriBuilder(baseUri)
        {
            Path = combinedPath,
            Query = queryString.HasValue ? queryString.Value![1..] : string.Empty
        }.Uri;
    }

    private static string NormalizeProxyPath(string path)
    {
        var normalizedPath = path.Trim('/');
        return string.IsNullOrWhiteSpace(normalizedPath) ? "/worker" : $"/{normalizedPath}";
    }
}