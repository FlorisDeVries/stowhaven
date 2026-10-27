using System.Net;
using System.Text;
using FlorisDeV.BackupMonitoring.Pages;
using FlorisDeV.BackupMonitoring.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FlorisDeV.BackupMonitoring.Tests;

public class MonitoringRenderingTests
{
    [Fact]
    public async Task SuccessfulOverviewDoesNotDisplayAnErrorBanner()
    {
        var html = await RenderAsync<Overview>();
        Assert.Contains("Your backup activity.", html);
        Assert.Contains("No commit jobs match this view.", html);
        Assert.DoesNotContain("role=\"alert\"", html);
    }

    [Fact]
    public async Task HistoryWithoutAContinuationDisablesNextPageAndHasNoErrorBanner()
    {
        var html = await RenderAsync<Commits>();
        Assert.Contains("Job history", html);
        Assert.Matches("aria-label=\"Next page\"[^>]*disabled", html);
        Assert.DoesNotContain("role=\"alert\"", html);
    }

    private static async Task<string> RenderAsync<T>() where T : IComponent
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var options = new MonitoringOptions
        {
            GatewayBaseUrl = "http://localhost:8200", UseDevelopmentAuthentication = true
        };
        options.Validate(true);
        services.AddSingleton(options);
        services.AddSingleton<AuthenticationStateProvider, LocalAuthenticationStateProvider>();
        services.AddSingleton(new MonitoringApi(new HttpClient(new EmptyCommitsHandler())
        {
            BaseAddress = new Uri("http://localhost:8200/")
        }));
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<T>();
            return component.ToHtmlString();
        });
    }

    private sealed class EmptyCommitsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"commits":[],"pageSize":25}""", Encoding.UTF8, "application/json")
            });
    }
}
