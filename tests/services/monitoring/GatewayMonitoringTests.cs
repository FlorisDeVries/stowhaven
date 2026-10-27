using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Gateway;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace FlorisDeV.BackupMonitoring.Tests;

public class GatewayMonitoringTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string GatewayClient = "22222222-2222-2222-2222-222222222222";
    private const string Operator = "33333333-3333-3333-3333-333333333333";
    private const string Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0";
    private const string Origin = "https://monitoring.example.com";
    private static readonly SymmetricSecurityKey SigningKey = new(Encoding.UTF8.GetBytes("stowhaven-test-signing-key-at-least-32-bytes-long"));

    [Fact]
    public async Task AnonymousAdminRequestIsRejectedEvenWithSpoofedEasyAuthHeaders()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL", "forged-principal");
        using var response = await client.GetAsync("/api/ops/commits");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("backup.access", Operator)]
    [InlineData("monitoring.administrator", Operator)]
    [InlineData("monitoring.admin", "44444444-4444-4444-4444-444444444444")]
    [InlineData("monitoring.admin", "")]
    public async Task OrdinaryClientsAndUnlistedUsersCannotAccessAdminRoutes(string scope, string user)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(scope, user));
        using var response = await client.GetAsync("/api/ops/manifests");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task EmptyOperatorAllowlistDeniesEvenAnAdminScopedUser()
    {
        await using var factory = CreateFactory(allowedUser: "");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("monitoring.admin", Operator));
        using var response = await client.GetAsync("/api/ops/commits");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("wrong-audience", false)]
    [InlineData(GatewayClient, true)]
    public async Task InvalidOrExpiredTokensAreRejected(string audience, bool expired)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("monitoring.admin", Operator, audience, expired));
        using var response = await client.GetAsync("/api/ops/commits");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AuthorizedOperatorCannotFallBackToManagedIdentityWhenOboIsMissing()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("monitoring.admin", Operator));
        using var response = await client.GetAsync("/api/ops/commits");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Theory]
    [InlineData(Origin, true)]
    [InlineData("https://untrusted.example.com", false)]
    public async Task PreflightAllowsOnlyTheConfiguredOrigin(string origin, bool expected)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/ops/commits");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "authorization");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(expected, response.Headers.Contains("Access-Control-Allow-Origin"));
        if (expected) Assert.Equal(Origin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task DevelopmentBypassCannotBeEnabledInProduction()
    {
        await using var factory = CreateFactory(allowDevelopment: true);
        using var client = factory.CreateClient();
        using var response = await client.PostAsync($"/api/ops/commits/{Guid.NewGuid()}/retry", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AdminAndClientRequestsUseSeparateDownstreamScopesAndTheAdminBearerIdentity()
    {
        var exchange = new RecordingExchange();
        var proxy = new RecordingProxy();
        await using var factory = CreateFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:OboClientSecret", "test-only-secret");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IOboTokenExchange>(exchange);
                services.AddHttpClient("gateway-proxy").ConfigurePrimaryHttpMessageHandler(() => proxy);
            });
        });
        using var client = factory.CreateClient();
        var assertion = Token("monitoring.admin", Operator);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", assertion);
        using var adminRequest = new HttpRequestMessage(HttpMethod.Get, "/api/ops/commits?status=Failed");
        // Even if another session token is present, use the bearer identity that
        // actually passed the admin policy.
        adminRequest.Headers.Add("X-MS-TOKEN-AAD-ACCESS-TOKEN", "unrelated-session-token");
        using var admin = await client.SendAsync(adminRequest);
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        Assert.EndsWith("/backup.admin", Assert.Single(exchange.LastScopes!));
        Assert.Equal(assertion, exchange.LastAssertion);
        Assert.Equal("/api/ops/commits?status=Failed", proxy.LastUri!.PathAndQuery);
        Assert.Equal("Bearer exchanged-test-token", proxy.LastAuthorization);
        Assert.Equal("no-store", admin.Headers.CacheControl?.ToString());

        using var normal = await client.GetAsync("/api/devices/123");
        Assert.Equal(HttpStatusCode.OK, normal.StatusCode);
        Assert.EndsWith("/backup.client", Assert.Single(exchange.LastScopes!));
        Assert.Equal("/api/devices/123", proxy.LastUri!.AbsolutePath);
    }

    private static WebApplicationFactory<global::Program> CreateFactory(string allowedUser = Operator, bool allowDevelopment = false)
        => new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Gateway:ApiBaseUrl", "http://127.0.0.1:9");
            builder.UseSetting("Gateway:WorkerBaseUrl", "http://127.0.0.1:9");
            builder.UseSetting("Gateway:OboTenantId", Tenant);
            builder.UseSetting("Gateway:OboClientId", GatewayClient);
            builder.UseSetting("Gateway:ApiTokenScope", $"api://{Guid.NewGuid()}/.default");
            builder.UseSetting("Gateway:AdminUserObjectIds:0", allowedUser);
            builder.UseSetting("Gateway:AllowedOrigins:0", Origin);
            builder.UseSetting("Gateway:AllowDevelopmentAdmin", allowDevelopment.ToString());
            builder.ConfigureTestServices(services => services.PostConfigure<JwtBearerOptions>(MonitoringAccess.Policy, options =>
            {
                var metadata = new OpenIdConnectConfiguration { Issuer = Issuer };
                metadata.SigningKeys.Add(SigningKey);
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
                options.TokenValidationParameters.IssuerSigningKey = SigningKey;
            }));
        });

    private static string Token(string scope, string user, string audience = GatewayClient, bool expired = false)
    {
        var now = DateTime.UtcNow;
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(Issuer, audience,
            [new Claim("tid", Tenant), new Claim("oid", user), new Claim("scp", scope)],
            notBefore: now.AddHours(-2), expires: expired ? now.AddHours(-1) : now.AddHours(1),
            signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)));
    }

    private sealed class RecordingExchange : IOboTokenExchange
    {
        public string[]? LastScopes { get; private set; }
        public string? LastAssertion { get; private set; }
        public Task<string> ExchangeAsync(OboOptions options, string userAssertion, CancellationToken cancellationToken)
        {
            LastScopes = options.ApiScopes;
            LastAssertion = userAssertion;
            return Task.FromResult("exchanged-test-token");
        }
    }

    private sealed class RecordingProxy : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }
        public string? LastAuthorization { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            LastAuthorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }
}
