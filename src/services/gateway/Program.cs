using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;

const string EasyAuthAccessTokenHeader = "X-MS-TOKEN-AAD-ACCESS-TOKEN";

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("gateway-proxy", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

var app = builder.Build();

var configuration = app.Services.GetRequiredService<IConfiguration>();
var apiBaseUrl = RequireAbsoluteUri(configuration["Gateway:ApiBaseUrl"], "Gateway:ApiBaseUrl");
var workerBaseUrl = RequireAbsoluteUri(configuration["Gateway:WorkerBaseUrl"], "Gateway:WorkerBaseUrl");
var gatewayHeaderName = configuration["Gateway:HeaderName"] ?? "X-Backup-Gateway";
var gatewayHeaderValue = configuration["Gateway:HeaderValue"] ?? string.Empty;
var apiTokenScope = configuration["Gateway:ApiTokenScope"];
var managedIdentityClientId = configuration["Gateway:ManagedIdentityClientId"];
var apiTokenCredential = string.IsNullOrWhiteSpace(apiTokenScope)
    ? null
    : CreateTokenCredential(managedIdentityClientId);

var oboClientId = configuration["Gateway:OboClientId"];
var oboClientSecret = configuration["Gateway:OboClientSecret"];
var oboTenantId = configuration["Gateway:OboTenantId"];
OboOptions? oboOptions = !string.IsNullOrWhiteSpace(oboClientId)
    && !string.IsNullOrWhiteSpace(oboClientSecret)
    && !string.IsNullOrWhiteSpace(oboTenantId)
    && !string.IsNullOrWhiteSpace(apiTokenScope)
    ? new OboOptions(oboClientId, oboClientSecret, oboTenantId)
    : null;

var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("GatewayStartup");
startupLogger.LogInformation("Gateway proxy auth mode: {Mode} (OboClientId={OboClientId}, ApiTokenScope={ApiTokenScope})",
    oboOptions is not null ? "OBO" : (apiTokenCredential is not null ? "ManagedIdentity" : "PassThrough"),
    string.IsNullOrWhiteSpace(oboClientId) ? "<missing>" : oboClientId,
    string.IsNullOrWhiteSpace(apiTokenScope) ? "<missing>" : apiTokenScope);

app.MapGet("/", () => Results.Redirect("/swagger"));
app.MapGet("/healthz", () => Results.Ok(new { status = "Healthy" }));

app.UseSwaggerUI(options =>
{
    options.RoutePrefix = "swagger";
    options.DocumentTitle = "Backup API Swagger";
    options.SwaggerEndpoint("/api/swagger/main/swagger.json", "FlorisDeV.BackupApi");
    options.SwaggerEndpoint("/worker/swagger/main/swagger.json", "FlorisDeV.BackupWorker");
});

app.Map("/api/{**path}", async context =>
{
    await ProxyAsync(context, apiBaseUrl, "/api", "/api", "/", gatewayHeaderName, gatewayHeaderValue, apiTokenCredential, apiTokenScope, oboOptions);
});

app.Map("/worker/{**path}", async context =>
{
    await ProxyAsync(context, workerBaseUrl, "/worker", string.Empty, "/worker", gatewayHeaderName, gatewayHeaderValue, tokenCredential: null, tokenScope: null);
});

app.Run();

static Uri RequireAbsoluteUri(string? value, string configurationKey)
{
    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
    {
        throw new InvalidOperationException($"Configuration value '{configurationKey}' must be an absolute URI.");
    }

    return uri;
}

static async Task ProxyAsync(
    HttpContext context,
    Uri baseUri,
    string proxyPrefix,
    string upstreamPathPrefix,
    string swaggerServerUrl,
    string gatewayHeaderName,
    string gatewayHeaderValue,
    TokenCredential? tokenCredential,
    string? tokenScope,
    OboOptions? oboOptions = null)
{
    var httpClientFactory = context.RequestServices.GetRequiredService<IHttpClientFactory>();
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("GatewayProxy");
    var httpClient = httpClientFactory.CreateClient("gateway-proxy");
    var routePath = context.Request.RouteValues["path"] as string;
    var targetUri = BuildProxyUri(baseUri, routePath, upstreamPathPrefix, context.Request.QueryString);
    var isSwaggerDocument = IsSwaggerDocument(routePath);

    using var requestMessage = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUri);
    requestMessage.Headers.TryAddWithoutValidation("X-Forwarded-Proto", context.Request.Scheme);
    requestMessage.Headers.TryAddWithoutValidation("X-Forwarded-Host", context.Request.Host.Value);
    requestMessage.Headers.TryAddWithoutValidation("X-Forwarded-Prefix", proxyPrefix);

    if (!string.IsNullOrWhiteSpace(gatewayHeaderValue))
    {
        requestMessage.Headers.TryAddWithoutValidation(gatewayHeaderName, gatewayHeaderValue);
    }

    var hasAuthorizationHeader = context.Request.Headers.ContainsKey("Authorization");
    var useManagedIdentity = tokenCredential is not null && !string.IsNullOrWhiteSpace(tokenScope) && !isSwaggerDocument;

    context.Request.Headers.TryGetValue(EasyAuthAccessTokenHeader, out var easyAuthToken);
    var hasEasyAuthToken = !isSwaggerDocument && !string.IsNullOrWhiteSpace(easyAuthToken.ToString());

    // Easy Auth only populates X-MS-TOKEN-AAD-ACCESS-TOKEN in session/cookie flows.
    // For direct Bearer token calls the original Authorization header is the user assertion.
    var userAssertionToken = hasEasyAuthToken
        ? easyAuthToken.ToString()
        : ExtractBearerToken(context.Request.Headers.Authorization.ToString());
    var canUseObo = !isSwaggerDocument && oboOptions is not null && !string.IsNullOrWhiteSpace(userAssertionToken);

    if (canUseObo)
    {
        // Exchange the user's gateway-scoped token for an API-scoped delegated token.
        // OBO preserves user identity claims (tid, oid) that app-only managed identity
        // tokens lack, which is required by user-specific API endpoints.
        var oboCredential = new OnBehalfOfCredential(oboOptions!.TenantId, oboOptions.ClientId, oboOptions.ClientSecret, userAssertionToken!);
        var oboResult = await oboCredential.GetTokenAsync(
            new TokenRequestContext([tokenScope!]),
            context.RequestAborted);
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", oboResult.Token);
        LogJwtClaims(logger, oboResult.Token, "OBO");
        logger.LogInformation("Proxying {Method} {Path} with OBO token (source: {Source})",
            context.Request.Method, context.Request.Path,
            hasEasyAuthToken ? "EasyAuth" : "Authorization");
    }
    else if (useManagedIdentity)
    {
        // No user token or OBO not configured; use Gateway managed identity for
        // service-to-service calls (health checks, Dapr, headless callers, etc.).
        var token = await tokenCredential!.GetTokenAsync(
            new TokenRequestContext([tokenScope!]),
            context.RequestAborted).ConfigureAwait(false);

        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        logger.LogInformation("Proxying {Method} {Path} with managed identity token (hasEasyAuthToken={HasEasyAuthToken}, canUseObo={CanUseObo})",
            context.Request.Method, context.Request.Path, hasEasyAuthToken, canUseObo);
    }
    else if (hasAuthorizationHeader)
    {
        logger.LogInformation("Proxying {Method} {Path} with incoming Authorization header", context.Request.Method, context.Request.Path);
    }

    if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
    {
        requestMessage.Content = new StreamContent(context.Request.Body);

        if (!string.IsNullOrWhiteSpace(context.Request.ContentType))
        {
            requestMessage.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(context.Request.ContentType);
        }
    }

    foreach (var header in context.Request.Headers)
    {
        if (ShouldSkipRequestHeader(header.Key))
        {
            continue;
        }

        // Suppress the incoming Authorization header whenever the Gateway sets its own
        // (OBO or managed identity) — the original carries the Gateway audience and
        // would be rejected by the upstream API.
        if ((useManagedIdentity || canUseObo) && string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
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

    if (isSwaggerDocument && responseMessage.IsSuccessStatusCode)
    {
        await RewriteSwaggerDocumentAsync(context, responseMessage, swaggerServerUrl).ConfigureAwait(false);
        return;
    }

    context.Response.StatusCode = (int)responseMessage.StatusCode;

    foreach (var header in responseMessage.Headers)
    {
        if (ShouldSkipResponseHeader(header.Key))
        {
            continue;
        }

        context.Response.Headers[header.Key] = header.Value.ToArray();
    }

    foreach (var header in responseMessage.Content.Headers)
    {
        if (ShouldSkipResponseHeader(header.Key))
        {
            continue;
        }

        context.Response.Headers[header.Key] = header.Value.ToArray();
    }

    context.Response.Headers.Remove("transfer-encoding");

    await responseMessage.Content.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
}

static Uri BuildProxyUri(Uri baseUri, string? path, string upstreamPathPrefix, QueryString queryString)
{
    var basePath = baseUri.AbsolutePath.TrimEnd('/');
    var targetPath = BuildTargetPath(path, upstreamPathPrefix);
    var combinedPath = string.IsNullOrEmpty(basePath) ? $"/{targetPath}" : $"{basePath}/{targetPath}";

    return new UriBuilder(baseUri)
    {
        Path = combinedPath,
        Query = queryString.HasValue ? queryString.Value![1..] : string.Empty
    }.Uri;
}

static TokenCredential CreateTokenCredential(string? managedIdentityClientId)
{
    return string.IsNullOrWhiteSpace(managedIdentityClientId)
        ? new ManagedIdentityCredential()
        : new ManagedIdentityCredential(managedIdentityClientId);
}

static string BuildTargetPath(string? path, string upstreamPathPrefix)
{
    var targetPath = path?.TrimStart('/') ?? string.Empty;

    // Swagger UI loads documents through /api/swagger/* and /worker/swagger/*,
    // but the upstream services expose those documents at /swagger/*.
    if (targetPath.StartsWith("swagger/", StringComparison.OrdinalIgnoreCase))
    {
        return targetPath;
    }

    var prefix = upstreamPathPrefix.Trim('/');
    if (string.IsNullOrEmpty(prefix))
    {
        return targetPath;
    }

    return string.IsNullOrEmpty(targetPath) ? prefix : $"{prefix}/{targetPath}";
}

static bool IsSwaggerDocument(string? path)
{
    var targetPath = path?.TrimStart('/') ?? string.Empty;
    return targetPath.StartsWith("swagger/", StringComparison.OrdinalIgnoreCase) &&
           targetPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
}

static async Task RewriteSwaggerDocumentAsync(HttpContext context, HttpResponseMessage responseMessage, string serverUrl)
{
    context.Response.StatusCode = (int)responseMessage.StatusCode;
    context.Response.ContentType = "application/json; charset=utf-8";

    var json = await responseMessage.Content.ReadAsStringAsync(context.RequestAborted).ConfigureAwait(false);
    var document = JsonNode.Parse(json)?.AsObject();

    if (document is null)
    {
        await context.Response.WriteAsync(json, context.RequestAborted).ConfigureAwait(false);
        return;
    }

    // Upstream services generate Swagger documents using their own Container App
    // host/scheme/path base. For Gateway-hosted Swagger UI, force try-it-out
    // requests to stay on the Gateway origin and use the public proxy prefix.
    document["servers"] = new JsonArray(new JsonObject
    {
        ["url"] = serverUrl
    });

    await context.Response.WriteAsync(
        document.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
        context.RequestAborted).ConfigureAwait(false);
}

static bool ShouldSkipRequestHeader(string headerName)
{
    if (IsHopByHopHeader(headerName) ||
        string.Equals(headerName, "Host", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(headerName, "Content-Length", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(headerName, "Accept-Encoding", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(headerName, "Cookie", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    return headerName.StartsWith("X-MS-TOKEN-", StringComparison.OrdinalIgnoreCase) ||
           headerName.StartsWith("X-MS-CLIENT-PRINCIPAL", StringComparison.OrdinalIgnoreCase);
}

static bool ShouldSkipResponseHeader(string headerName)
{
    return IsHopByHopHeader(headerName) ||
           string.Equals(headerName, "Set-Cookie", StringComparison.OrdinalIgnoreCase);
}

static bool IsHopByHopHeader(string headerName)
{
    return string.Equals(headerName, "Connection", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(headerName, "Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(headerName, "Proxy-Authenticate", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(headerName, "Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(headerName, "TE", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(headerName, "Trailer", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(headerName, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(headerName, "Upgrade", StringComparison.OrdinalIgnoreCase);
}

static string? ExtractBearerToken(string? authorizationHeader) =>
    authorizationHeader?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
        ? authorizationHeader[7..].Trim()
        : null;

static void LogJwtClaims(ILogger logger, string token, string label)
{
    try
    {
        var parts = token.Split('.');
        if (parts.Length < 2) return;
        var padding = (4 - parts[1].Length % 4) % 4;
        var payload = System.Text.Encoding.UTF8.GetString(
            Convert.FromBase64String(parts[1] + new string('=', padding)));
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        logger.LogInformation("{Label} token claims — aud: {Aud}, scp: {Scp}, roles: {Roles}, oid: {Oid}",
            label,
            root.TryGetProperty("aud", out var aud) ? aud.ToString() : "<missing>",
            root.TryGetProperty("scp", out var scp) ? scp.ToString() : "<missing>",
            root.TryGetProperty("roles", out var roles) ? roles.ToString() : "<missing>",
            root.TryGetProperty("oid", out var oid) ? oid.GetString()?[..8] + "..." : "<missing>");
    }
    catch { /* best-effort */ }
}

record OboOptions(string ClientId, string ClientSecret, string TenantId);
