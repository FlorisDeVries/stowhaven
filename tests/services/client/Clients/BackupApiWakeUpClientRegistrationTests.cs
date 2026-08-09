using System.Net;
using Azure.Core;
using FlorisDeV.BackupClient.Clients.BackupApi;
using FlorisDeV.BackupClient.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace FlorisDeV.BackupClient.Tests.Clients;

public sealed class BackupApiWakeUpClientRegistrationTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddBackupApi_WakeUpClientSendsAuthenticatedProbe()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BackupApiClient:ApiUrl"] = "https://backup.example.test",
                ["BackupApiClient:AuthenticationScope"] = "api://backup/backup.access",
                ["BackupApiClient:AuthenticationTenant"] = "test-tenant",
                ["BackupApiClient:RetryOptions:Retry:MaxRetryAttempts"] = "3",
                ["BackupClient:HttpTimeoutSeconds"] = "300"
            })
            .Build();
        var credential = new Mock<TokenCredential>();
        credential.Setup(c => c.GetTokenAsync(
                It.IsAny<TokenRequestContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccessToken("wake-up-token", DateTimeOffset.UtcNow.AddHours(1)));
        var terminalHandler = new CaptureAuthorizationHandler();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(credential.Object);
        services.AddBackupApi(configuration);
        services.AddHttpClient(ApiWakeUpService.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => terminalHandler);
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(ApiWakeUpService.HttpClientName);

        using var response = await client.GetAsync(ApiWakeUpService.HealthPath);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        terminalHandler.AuthorizationScheme.Should().Be("Bearer");
        terminalHandler.AuthorizationParameter.Should().Be("wake-up-token");
    }

    private sealed class CaptureAuthorizationHandler : HttpMessageHandler
    {
        public string? AuthorizationScheme { get; private set; }

        public string? AuthorizationParameter { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
