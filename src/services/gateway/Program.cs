using System.Net.Http.Headers;

const string EasyAuthAccessTokenHeader = "X-MS-TOKEN-AAD-ACCESS-TOKEN";

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("swagger-proxy", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

var app = builder.Build();

var configuration = app.Services.GetRequiredService<IConfiguration>();
var apiBaseUrl = RequireAbsoluteUri(configuration["Gateway:ApiBaseUrl"], "Gateway:ApiBaseUrl");
var workerBaseUrl = RequireAbsoluteUri(configuration["Gateway:WorkerBaseUrl"], "Gateway:WorkerBaseUrl");
var gatewayHeaderName = configuration["Gateway:HeaderName"] ?? "X-Backup-Gateway";
var gatewayHeaderValue = configuration["Gateway:HeaderValue"] ?? string.Empty;

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
    await ProxyAsync(context, apiBaseUrl, "/api", gatewayHeaderName, gatewayHeaderValue);
});

app.Map("/worker/{**path}", async context =>
{
    await ProxyAsync(context, workerBaseUrl, "/worker", gatewayHeaderName, gatewayHeaderValue);
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
    string forwardedPrefix,
    string gatewayHeaderName,
    string gatewayHeaderValue)
{
    var httpClientFactory = context.RequestServices.GetRequiredService<IHttpClientFactory>();
    var httpClient = httpClientFactory.CreateClient("swagger-proxy");
    var routePath = context.Request.RouteValues["path"] as string;
    var targetUri = BuildProxyUri(baseUri, routePath, forwardedPrefix, context.Request.QueryString);

    using var requestMessage = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUri);
    requestMessage.Headers.Host = targetUri.Authority;
    requestMessage.Headers.TryAddWithoutValidation("X-Forwarded-Proto", context.Request.Scheme);
    requestMessage.Headers.TryAddWithoutValidation("X-Forwarded-Host", context.Request.Host.Value);
    requestMessage.Headers.TryAddWithoutValidation("X-Forwarded-Prefix", forwardedPrefix);

    if (!string.IsNullOrWhiteSpace(gatewayHeaderValue))
    {
        requestMessage.Headers.TryAddWithoutValidation(gatewayHeaderName, gatewayHeaderValue);
    }

    if (!context.Request.Headers.ContainsKey("Authorization") &&
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
        if (string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
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
}

static Uri BuildProxyUri(Uri baseUri, string? path, string forwardedPrefix, QueryString queryString)
{
    var basePath = baseUri.AbsolutePath.TrimEnd('/');
    var targetPath = BuildTargetPath(path, forwardedPrefix);
    var combinedPath = string.IsNullOrEmpty(basePath) ? $"/{targetPath}" : $"{basePath}/{targetPath}";

    return new UriBuilder(baseUri)
    {
        Path = combinedPath,
        Query = queryString.HasValue ? queryString.Value![1..] : string.Empty
    }.Uri;
}

static string BuildTargetPath(string? path, string forwardedPrefix)
{
    var targetPath = path?.TrimStart('/') ?? string.Empty;

    // Swagger UI loads documents through /api/swagger/* and /worker/swagger/*,
    // but the upstream services expose those documents at /swagger/*.
    if (targetPath.StartsWith("swagger/", StringComparison.OrdinalIgnoreCase))
    {
        return targetPath;
    }

    var prefix = forwardedPrefix.Trim('/');
    return string.IsNullOrEmpty(targetPath) ? prefix : $"{prefix}/{targetPath}";
}