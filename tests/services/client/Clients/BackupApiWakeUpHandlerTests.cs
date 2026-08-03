using System.Net;
using FlorisDeV.BackupClient.Clients.BackupApi;
using FlorisDeV.BackupClient.Services;
using FluentAssertions;
using Moq;

namespace FlorisDeV.BackupClient.Tests.Clients;

public sealed class BackupApiWakeUpHandlerTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task SendAsync_ProtectedApiCall_WakesBeforeSendingRequest()
    {
        var calls = new List<string>();
        var wakeUpService = new Mock<IApiWakeUpService>();
        wakeUpService.Setup(x => x.EnsureApiAwakeAsync(It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("wake"))
            .Returns(Task.CompletedTask);
        using var handler = new BackupApiWakeUpHandler(wakeUpService.Object)
        {
            InnerHandler = new CallbackHandler((_, _) =>
            {
                calls.Add("request");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            })
        };
        using var invoker = new HttpMessageInvoker(handler);

        using var response = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "https://backup.example.test/api/devices"),
            CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        calls.Should().Equal("wake", "request");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SendAsync_HealthProbe_DoesNotWakeRecursively()
    {
        var wakeUpService = new Mock<IApiWakeUpService>();
        using var handler = new BackupApiWakeUpHandler(wakeUpService.Object)
        {
            InnerHandler = new CallbackHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))
        };
        using var invoker = new HttpMessageInvoker(handler);

        using var response = await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"https://backup.example.test{ApiWakeUpService.HealthPath}"),
            CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        wakeUpService.Verify(
            x => x.EnsureApiAwakeAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SendAsync_WhenWakeUpFails_DoesNotSendProtectedRequest()
    {
        var wakeUpService = new Mock<IApiWakeUpService>();
        wakeUpService.Setup(x => x.EnsureApiAwakeAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("wake-up failed"));
        var requestSent = false;
        using var handler = new BackupApiWakeUpHandler(wakeUpService.Object)
        {
            InnerHandler = new CallbackHandler((_, _) =>
            {
                requestSent = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            })
        };
        using var invoker = new HttpMessageInvoker(handler);

        var act = () => invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://backup.example.test/api/devices"),
            CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>().WithMessage("wake-up failed");
        requestSent.Should().BeFalse();
    }

    private sealed class CallbackHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => callback(request, cancellationToken);
    }
}
