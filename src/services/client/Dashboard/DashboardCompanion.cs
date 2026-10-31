using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlorisDeV.BackupClient.Dashboard;

public sealed record DashboardConnection(string Address, string ControlToken);

/// <summary>A separate, persistent observation host. It never resolves or invokes backup services.</summary>
public sealed class DashboardCompanion : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly FileStream _ownership;
    private readonly string _connectionPath;
    public Uri Address { get; }
    private DashboardCompanion(WebApplication app, Uri address, FileStream ownership, string connectionPath)
        => (_app, Address, _ownership, _connectionPath) = (app, address, ownership, connectionPath);

    public static async Task<DashboardCompanion> StartAsync(DashboardOptions options, TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)
    {
        var time = timeProvider ?? TimeProvider.System;
        PrivateFiles.EnsureDirectory(options.ReportingDirectory);
        var lockPath = Path.Combine(options.ReportingDirectory, "companion.lock");
        PrivateFiles.RejectLink(lockPath);
        var create = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) create.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var ownership = new FileStream(lockPath, create);
        WebApplication? app = null;
        try
        {
            var controlToken = Token();
            var tickets = new ConcurrentDictionary<string, DateTimeOffset>();
            var sessions = new ConcurrentDictionary<string, DateTimeOffset>();
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(DashboardCompanion).Assembly.FullName });
            builder.Logging.ClearProviders(); // Bootstrap/session credentials never enter HTTP logs.
            builder.WebHost.ConfigureKestrel(server =>
            {
                server.Listen(IPAddress.Loopback, options.Port);
                server.Limits.MaxRequestBodySize = 4096;
            });
            app = builder.Build();
            string? expectedOrigin = null;
            app.Use(async (context, next) =>
            {
                var origin = context.Request.Headers.Origin.ToString();
                if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote) ||
                    !string.Equals(context.Request.Host.Value, new Uri(expectedOrigin!).Authority, StringComparison.Ordinal) ||
                    (origin.Length > 0 && !string.Equals(origin, expectedOrigin, StringComparison.Ordinal)) ||
                    context.Request.Headers["Sec-Fetch-Site"].ToString() is "cross-site")
                { context.Response.StatusCode = 403; return; }
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
                await next(context);
            });
            app.MapGet("/", () => Results.Content(DashboardView.Html, "text/html"));
            app.MapGet("/dashboard.js", () => Results.Content(DashboardView.JavaScript, "application/javascript"));
            app.MapGet("/dashboard.css", () => Results.Content(DashboardView.Css, "text/css"));
            app.MapPost("/api/prove", async (HttpContext context) =>
            {
                if (context.Request.Headers.Origin.Count != 0) return Results.StatusCode(403);
                ProofChallenge? challenge;
                try { challenge = await context.Request.ReadFromJsonAsync<ProofChallenge>(cancellationToken: context.RequestAborted); }
                catch (JsonException) { return Results.BadRequest(); }
                if (challenge?.Nonce is not { Length: 64 }) return Results.BadRequest();
                return Results.Json(new ProofResponse(Proof(controlToken, challenge.Nonce)));
            });
            app.MapPost("/api/open", (HttpContext context) =>
            {
                // Only the same-user CLI with a private control credential can mint browser grants.
                if (context.Request.Headers.Origin.Count != 0 || !FixedEquals(context.Request.Headers.Authorization.ToString(), "Bearer " + controlToken))
                    return Results.Unauthorized();
                var now = time.GetUtcNow();
                foreach (var expired in tickets.Where(t => t.Value <= now)) tickets.TryRemove(expired.Key, out _);
                if (tickets.Count >= 32) return Results.StatusCode(429);
                var ticket = Token();
                tickets[ticket] = now.AddMinutes(1);
                return Results.Json(new { url = expectedOrigin + "/#" + ticket });
            });
            app.MapPost("/api/session", async (HttpContext context) =>
            {
                if (!string.Equals(context.Request.ContentType?.Split(';')[0], "application/json", StringComparison.OrdinalIgnoreCase))
                    return Results.StatusCode(415);
                SessionGrant? grant;
                try { grant = await context.Request.ReadFromJsonAsync<SessionGrant>(cancellationToken: context.RequestAborted); }
                catch (JsonException) { return Results.BadRequest(); }
                if (grant?.Ticket is not { Length: 64 } ticket || !tickets.TryRemove(ticket, out var expiry) || expiry <= time.GetUtcNow())
                    return Results.Unauthorized();
                var now = time.GetUtcNow();
                foreach (var expired in sessions.Where(s => now - s.Value >= TimeSpan.FromHours(12))) sessions.TryRemove(expired.Key, out _);
                if (sessions.Count >= 32) return Results.StatusCode(429);
                var session = Token();
                sessions[session] = now;
                return Results.Json(new { session });
            });
            bool Authorized(HttpContext context, bool activity)
            {
                var now = time.GetUtcNow();
                var authorization = context.Request.Headers.Authorization.ToString();
                var session = authorization.StartsWith("Bearer ", StringComparison.Ordinal) ? authorization[7..] : "";
                if (session.Length != 64 || !sessions.TryGetValue(session, out var last) || now - last >= TimeSpan.FromHours(12))
                {
                    if (session != null) sessions.TryRemove(session, out _);
                    return false;
                }
                if (activity) sessions[session] = now;
                return true;
            }
            app.MapPost("/api/activity", (HttpContext context) =>
                Authorized(context, activity: true) ? Results.NoContent() : Results.Unauthorized());
            app.MapGet("/api/status", async (HttpContext context) =>
            {
                if (!Authorized(context, activity: false)) return Results.Unauthorized();
                return Results.Json(await DashboardReporting.ReadAsync(options, time), DashboardReporting.Json);
            });
            app.MapGet("/api/logs", async (HttpContext context) =>
            {
                if (!Authorized(context, activity: false)) return Results.Unauthorized();
                if (context.Request.QueryString.HasValue) return Results.BadRequest();
                return Results.Json(await DashboardRecentLogs.ReadAsync(options, context.RequestAborted), DashboardReporting.Json);
            });
            await app.StartAsync(cancellationToken);
            var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            expectedOrigin = address.GetLeftPart(UriPartial.Authority);
            var connectionPath = Path.Combine(options.ReportingDirectory, "companion.json");
            await PrivateFiles.ReplaceAsync(connectionPath, JsonSerializer.Serialize(new DashboardConnection(expectedOrigin, controlToken), DashboardReporting.Json));
            return new(app, address, ownership, connectionPath);
        }
        catch
        {
            if (app != null) await app.DisposeAsync();
            ownership.Dispose();
            throw;
        }
    }

    /// <summary>Same-user command boundary: obtains a short-lived, single-use browser authorization URL.</summary>
    public static async Task<Uri> OpenAsync(DashboardOptions options, CancellationToken cancellationToken = default)
    {
        PrivateFiles.EnsureDirectory(options.ReportingDirectory);
        var connection = JsonSerializer.Deserialize<DashboardConnection>(await PrivateFiles.ReadAsync(Path.Combine(options.ReportingDirectory, "companion.json")), DashboardReporting.Json)
            ?? throw new IOException("Dashboard connection is unavailable. Start the dashboard companion.");
        if (!Uri.TryCreate(connection.Address, UriKind.Absolute, out var address) || address.Scheme != "http" || address.Host != "127.0.0.1")
            throw new IOException("Dashboard connection must use IPv4 loopback.");
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(5) };
        // Prove the listener knows this private instance credential before sending it anything reusable.
        // This prevents a different user taking over a stopped companion's former loopback port.
        var nonce = Token();
        using var proofResponse = await client.PostAsJsonAsync("/api/prove", new ProofChallenge(nonce), cancellationToken);
        proofResponse.EnsureSuccessStatusCode();
        var proof = await proofResponse.Content.ReadFromJsonAsync<ProofResponse>(cancellationToken);
        if (proof?.Proof is not { Length: 64 } || !FixedEquals(proof.Proof, Proof(connection.ControlToken, nonce)))
            throw new IOException("The listener is not the recorded dashboard companion. Restart the companion.");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/open");
        request.Headers.Authorization = new("Bearer", connection.ControlToken);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var grant = await response.Content.ReadFromJsonAsync<OpenGrant>(cancellationToken);
        if (!Uri.TryCreate(grant?.Url, UriKind.Absolute, out var url) || url.GetLeftPart(UriPartial.Authority) != address.GetLeftPart(UriPartial.Authority))
            throw new IOException("Dashboard authorization response is invalid.");
        return url;
    }

    public Task WaitForShutdownAsync(CancellationToken token = default) => _app.WaitForShutdownAsync(token);
    public async ValueTask DisposeAsync()
    {
        try { await _app.StopAsync(); await _app.DisposeAsync(); }
        finally { File.Delete(_connectionPath); _ownership.Dispose(); }
    }
    private static string Token() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static bool FixedEquals(string left, string right) => CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(left), System.Text.Encoding.UTF8.GetBytes(right));
    private static string Proof(string key, string nonce) => Convert.ToHexString(HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key), System.Text.Encoding.UTF8.GetBytes(nonce)));
    private sealed record ProofChallenge(string Nonce);
    private sealed record ProofResponse(string Proof);
    private sealed record SessionGrant(string Ticket);
    private sealed record OpenGrant(string Url);
}
