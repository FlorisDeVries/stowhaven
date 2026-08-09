using System.Net;
using FlorisDeV.BackupClient.Clients.BackupApi.Config;
using FlorisDeV.BackupClient.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace FlorisDeV.BackupClient.Tests.Services;

public sealed class ApiWakeUpServiceTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task EnsureApiAwakeAsync_SuccessfulProbeIsReusedWithinFreshnessWindow()
    {
        var probeCount = 0;
        using var client = CreateClient((request, _) =>
        {
            request.RequestUri!.AbsolutePath.Should().Be(ApiWakeUpService.HealthPath);
            Interlocked.Increment(ref probeCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var sut = CreateService(client);

        await sut.EnsureApiAwakeAsync(CancellationToken.None);
        await sut.EnsureApiAwakeAsync(CancellationToken.None);

        probeCount.Should().Be(1);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task EnsureApiAwakeAsync_AnonymousAuthResponse_ConfirmsGatewayIsReachable(
        HttpStatusCode statusCode)
    {
        var probeCount = 0;
        using var client = CreateClient((_, _) =>
        {
            Interlocked.Increment(ref probeCount);
            return Task.FromResult(new HttpResponseMessage(statusCode));
        });
        var sut = CreateService(client);

        await sut.EnsureApiAwakeAsync(CancellationToken.None);
        await sut.EnsureApiAwakeAsync(CancellationToken.None);

        probeCount.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task EnsureApiAwakeAsync_ConcurrentCallsShareOneProbe()
    {
        var probeCount = 0;
        var probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = CreateClient(async (_, cancellationToken) =>
        {
            Interlocked.Increment(ref probeCount);
            probeStarted.SetResult();
            await releaseProbe.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var sut = CreateService(client);

        var first = sut.EnsureApiAwakeAsync(CancellationToken.None);
        await probeStarted.Task;
        var second = sut.EnsureApiAwakeAsync(CancellationToken.None);
        releaseProbe.SetResult();
        await Task.WhenAll(first, second);

        probeCount.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task EnsureApiAwakeAsync_WhenProbeFailsUntilBudgetExpires_ThrowsTimeout()
    {
        var probeCount = 0;
        using var client = CreateClient((_, _) =>
        {
            Interlocked.Increment(ref probeCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });
        var sut = CreateService(client, new ApiWakeUpOptions
        {
            Enabled = true,
            InitialDelaySeconds = 1,
            MaxDelaySeconds = 1,
            MaxWaitSeconds = 1,
            ProbeTimeoutSeconds = 1,
            RecheckIntervalSeconds = 60
        });

        var act = () => sut.EnsureApiAwakeAsync(CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>()
            .WithMessage("*did not respond within 1s*");
        probeCount.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task EnsureApiAwakeAsync_WhenDisabled_DoesNotCreateProbeClient()
    {
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        var sut = new ApiWakeUpService(
            factory.Object,
            CreateOptions(new ApiWakeUpOptions { Enabled = false }),
            NullLogger<ApiWakeUpService>.Instance);

        await sut.EnsureApiAwakeAsync(CancellationToken.None);

        factory.VerifyNoOtherCalls();
    }

    private static ApiWakeUpService CreateService(
        HttpClient client,
        ApiWakeUpOptions? wakeUpOptions = null)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient(ApiWakeUpService.HttpClientName)).Returns(client);
        return new ApiWakeUpService(
            factory.Object,
            CreateOptions(wakeUpOptions ?? new ApiWakeUpOptions()),
            NullLogger<ApiWakeUpService>.Instance);
    }

    private static IOptions<BackupApiClientOptions> CreateOptions(ApiWakeUpOptions wakeUpOptions)
        => Options.Create(new BackupApiClientOptions
        {
            ApiUrl = "https://backup.example.test",
            AuthenticationScope = "api://backup/access",
            AuthenticationTenant = "tenant",
            WakeUp = wakeUpOptions
        });

    private static HttpClient CreateClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback)
        => new(new CallbackHandler(callback))
        {
            BaseAddress = new Uri("https://backup.example.test"),
            Timeout = Timeout.InfiniteTimeSpan
        };

    private sealed class CallbackHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => callback(request, cancellationToken);
    }
}
