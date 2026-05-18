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
    await ProxyAsync(context, apiBaseUrl, "/api", "/api", "/", gatewayHeaderName, gatewayHeaderValue, apiTokenCredential, apiTokenScope);
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
    string? tokenScope)
{
    var httpClientFactory = context.RequestServices.GetRequiredService<IHttpClientFactory>();
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

    if (tokenCredential is not null &&
        !string.IsNullOrWhiteSpace(tokenScope) &&
        !isSwaggerDocument)
    {
        // Easy Auth injects a token for the Gateway app registration. The API
        // expects its own audience/app role, so API proxy calls must use the
        // Gateway managed identity instead of forwarding the Easy Auth token.
        var token = await tokenCredential.GetTokenAsync(
            new TokenRequestContext([tokenScope]),
            context.RequestAborted).ConfigureAwait(false);

        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    }
    else if (!context.Request.Headers.ContainsKey("Authorization") &&
             context.Request.Headers.TryGetValue(EasyAuthAccessTokenHeader, out var accessToken) &&
             !string.IsNullOrWhiteSpace(accessToken.ToString()))
    {
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.ToString());
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
