using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlorisDeV.Gateway.Tests;

public class GatewayProxyTests
{
    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task ForwardsMethodPathQueryBodyAndResponse(string method)
    {
        using var factory = new Factory(async (request, token) =>
        {
            Assert.Equal(method, request.Method.Method);
            Assert.Equal("https://upstream.test/api/devices?id=42", request.RequestUri!.AbsoluteUri);
            Assert.Equal("sample", await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.False(request.Headers.Contains("X-MS-CLIENT-PRINCIPAL"));
            var response = new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("upstream-result") };
            response.Headers.Add("X-Result", "retained");
            response.Headers.Add("Set-Cookie", "private=value");
            return response;
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/devices?id=42") { Content = new StringContent("sample") };
        request.Headers.Authorization = new("Bearer", "test-token");
        request.Headers.Add("Cookie", "private=value");
        request.Headers.Add("X-MS-CLIENT-PRINCIPAL", "untrusted");
        using var result = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, result.StatusCode);
        Assert.Equal("upstream-result", await result.Content.ReadAsStringAsync());
        Assert.Equal("retained", Assert.Single(result.Headers.GetValues("X-Result")));
        Assert.False(result.Headers.Contains("Set-Cookie"));
    }

    [Theory]
    [InlineData("/api/swagger/main/swagger.json", "/swagger/main/swagger.json", "/")]
    [InlineData("/worker/swagger/main/swagger.json", "/swagger/main/swagger.json", "/worker")]
    public async Task SwaggerUsesPublicServerPrefix(string path, string upstreamPath, string server)
    {
        using var factory = new Factory((request, _) =>
        {
            Assert.Equal(upstreamPath, request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{\"openapi\":\"3.0.1\",\"servers\":[{\"url\":\"https://internal\"}]}", Encoding.UTF8, "application/json") });
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(server, document.RootElement.GetProperty("servers")[0].GetProperty("url").GetString());
    }

    [Fact]
    public async Task UpstreamAuthenticationFailureIsPreserved()
    {
        using var factory = new Factory((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("authentication required") };
            response.Headers.WwwAuthenticate.Add(new("Bearer"));
            return Task.FromResult(response);
        });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/devices");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
    }

    [Fact]
    public async Task AdministrativeRouteRejectsUnauthenticatedRequestBeforeProxying()
    {
        var forwarded = false;
        using var factory = new Factory((_, _) => { forwarded = true; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/ops/commits");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(forwarded);
    }

    [Fact]
    public async Task UnreachableUpstreamReturnsBadGateway()
    {
        using var factory = new Factory((_, _) => throw new HttpRequestException("Unavailable"));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/devices");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task UpstreamTimeoutReturnsGatewayTimeout()
    {
        using var factory = new Factory(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, timeoutSeconds: "0.1");
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/devices");
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }

    [Fact]
    public async Task ClientCancellationCancelsUpstreamRequest()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var factory = new Factory(async (_, token) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = factory.CreateClient();
        using var cts = new CancellationTokenSource();
        var request = client.GetAsync("/api/devices", cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class Factory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, string timeoutSeconds = "10")
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:ApiBaseUrl"] = "https://upstream.test", ["Gateway:WorkerBaseUrl"] = "https://worker.test",
                ["Gateway:ApiTokenScope"] = "", ["Gateway:OboClientId"] = "", ["Gateway:OboTenantId"] = "",
                ["Gateway:OboClientSecret"] = "", ["Gateway:AllowDevelopmentAdmin"] = "false",
                ["Gateway:ProxyTimeoutSeconds"] = timeoutSeconds
            }));
            builder.ConfigureTestServices(services => services.AddHttpClient("gateway-proxy")
                .ConfigurePrimaryHttpMessageHandler(() => new Handler(send)));
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
