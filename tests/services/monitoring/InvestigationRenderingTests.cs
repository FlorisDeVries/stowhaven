using System.Net;
using System.Text;
using System.Text.Json;
using FlorisDeV.BackupContracts.Api.Responses;
using FlorisDeV.BackupContracts.State;
using FlorisDeV.BackupMonitoring.Pages;
using FlorisDeV.BackupMonitoring.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FlorisDeV.BackupMonitoring.Tests;

public sealed class InvestigationRenderingTests
{
    private static readonly Guid DeviceId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public async Task DeviceListingLinksToTheOwnerAndInvestigationWithoutShowingFalseErrors()
    {
        var html = await RenderAsync<Devices>(new ListDevicesResponse
        {
            TenantId = "tenant-a", PageSize = 25, Devices = [Device()]
        });
        Assert.Contains("Test workstation", html);
        Assert.Contains($"href=\"devices/{DeviceId}\"", html);
        Assert.Contains("href=\"users/owner-a\"", html);
        Assert.DoesNotContain("role=\"alert\"", html);
        Assert.Matches("aria-label=\"Next page\"[^>]*disabled", html);
    }

    [Fact]
    public async Task UsersListingExplainsRegisteredOwnerScopeAndLinksToDeviceOwnership()
    {
        var html = await RenderAsync<Users>(new ListBackupUsersResponse
        {
            TenantId = "tenant-a", PageSize = 25,
            Users = [new BackupUserResponse { TenantId = "tenant-a", UserId = "owner-a" }]
        });
        Assert.Contains("Registered backup users", html);
        Assert.Contains("href=\"users/owner-a\"", html);
        Assert.Contains("directory profile names and email addresses are not collected", html);
        Assert.DoesNotContain("role=\"alert\"", html);
    }

    [Fact]
    public async Task DeviceStateShowsMissingHistoryAndDoesNotClaimTheDeviceIsOnline()
    {
        var html = await RenderAsync<DeviceDetails>(new DeviceStateResponse
        {
            Device = Device(), ObservedAt = DateTimeOffset.UtcNow
        }, new() { [nameof(DeviceDetails.DeviceId)] = DeviceId });
        Assert.Contains("Test workstation", html);
        Assert.Contains("No matching run recorded.", html);
        Assert.Contains("No commit job has been recorded", html);
        Assert.Contains("Older runs may predate commit leases", html);
        Assert.Contains("local scheduler state", html);
        Assert.DoesNotContain("role=\"alert\"", html);
    }

    private static DeviceRegistrationResponse Device() => new()
    {
        DeviceId = DeviceId, DisplayName = "Test workstation", TenantId = "tenant-a", UserId = "owner-a",
        Status = DeviceRegistrationStatus.Active, CreatedAt = DateTimeOffset.UtcNow.AddDays(-7), LastSeenAt = DateTimeOffset.UtcNow
    };

    private static async Task<string> RenderAsync<T>(object response, Dictionary<string, object?>? parameters = null) where T : IComponent
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var options = new MonitoringOptions { GatewayBaseUrl = "http://localhost:8200", UseDevelopmentAuthentication = true };
        options.Validate(true);
        services.AddSingleton(options);
        services.AddSingleton<NavigationManager, TestNavigation>();
        services.AddSingleton<AuthenticationStateProvider, LocalAuthenticationStateProvider>();
        services.AddSingleton(new MonitoringApi(new HttpClient(new JsonHandler(response)) { BaseAddress = new Uri("http://localhost:8200/") }));
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters ?? new()));
            return component.ToHtmlString();
        });
    }

    private sealed class JsonHandler(object response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json") });
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
}
