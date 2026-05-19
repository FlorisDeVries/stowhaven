using System.Net;
using Azure.Core;
using FluentAssertions;
using FlorisDeV.BackupClient.Clients.BackupApi;
using FlorisDeV.BackupClient.Clients.BackupApi.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace FlorisDeV.BackupClient.Tests.Clients;

/// <summary>
/// Tests for BackupApiAuthHandler that verifies authentication header injection.
/// </summary>
public class BackupApiAuthHandlerTests
{
    private readonly Mock<IOptionsSnapshot<BackupApiClientOptions>> _mockOptions;
    private readonly Mock<TokenCredential> _mockCredential;
    private readonly Mock<ILogger<BackupApiAuthHandler>> _mockLogger;
    private readonly BackupApiClientOptions _clientOptions;

    public BackupApiAuthHandlerTests()
    {
        _clientOptions = new BackupApiClientOptions
        {
            ApiUrl = "https://api.example.com",
            AuthenticationScope = "api://test-api/backup.admin",
            AuthenticationTenant = "test-tenant-id"
        };

        _mockOptions = new Mock<IOptionsSnapshot<BackupApiClientOptions>>();
        _mockOptions.Setup(o => o.Value).Returns(_clientOptions);

        _mockCredential = new Mock<TokenCredential>();

        _mockLogger = new Mock<ILogger<BackupApiAuthHandler>>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SendAsync_ShouldRequestTokenWithCorrectScopeAndTenant()
    {
        // Arrange
        var expectedToken = new AccessToken("test-access-token", DateTimeOffset.UtcNow.AddHours(1));
        TokenRequestContext? capturedContext = null;

        _mockCredential
            .Setup(c => c.GetTokenAsync(It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()))
            .Callback<TokenRequestContext, CancellationToken>((context, _) => capturedContext = context)
            .ReturnsAsync(expectedToken);

        var handler = new BackupApiAuthHandler(_mockOptions.Object, _mockCredential.Object, _mockLogger.Object)
        {
            InnerHandler = new TestHttpMessageHandler()
        };

        using var client = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/test");

        // Act
        await client.SendAsync(request);

        // Assert
        capturedContext.Should().NotBeNull();
        capturedContext!.Value.Scopes.Should().ContainSingle()
            .Which.Should().Be("api://test-api/backup.admin");
        capturedContext.Value.TenantId.Should().Be("test-tenant-id");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SendAsync_ShouldSetAuthorizationHeaderWithToken()
    {
        // Arrange
        var expectedToken = new AccessToken("test-access-token-123", DateTimeOffset.UtcNow.AddHours(1));
        _mockCredential
            .Setup(c => c.GetTokenAsync(It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedToken);

        var testHandler = new TestHttpMessageHandler();
        var handler = new BackupApiAuthHandler(_mockOptions.Object, _mockCredential.Object, _mockLogger.Object)
        {
            InnerHandler = testHandler
        };

        using var client = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/backup/start");

        // Act
        await client.SendAsync(request);

        // Assert
        testHandler.CapturedRequest.Should().NotBeNull();
        testHandler.CapturedRequest!.Headers.Authorization.Should().NotBeNull();
        testHandler.CapturedRequest.Headers.Authorization!.Scheme.Should().Be("Bearer");
        testHandler.CapturedRequest.Headers.Authorization.Parameter.Should().Be("test-access-token-123");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SendAsync_ShouldUseBearerTokenType()
    {
        // Arrange
        var expectedToken = new AccessToken("test-bearer-token", DateTimeOffset.UtcNow.AddHours(1));
        _mockCredential
            .Setup(c => c.GetTokenAsync(It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedToken);

        var testHandler = new TestHttpMessageHandler();
        var handler = new BackupApiAuthHandler(_mockOptions.Object, _mockCredential.Object, _mockLogger.Object)
        {
            InnerHandler = testHandler
        };

        using var client = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/test");

        // Act
        await client.SendAsync(request);

        // Assert
        testHandler.CapturedRequest.Should().NotBeNull();
        testHandler.CapturedRequest!.Headers.Authorization.Should().NotBeNull();
        testHandler.CapturedRequest.Headers.Authorization!.Scheme.Should().Be("Bearer");
        testHandler.CapturedRequest.Headers.Authorization.Parameter.Should().Be("test-bearer-token");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SendAsync_ShouldForwardRequestToInnerHandler()
    {
        // Arrange
        var expectedToken = new AccessToken("token", DateTimeOffset.UtcNow.AddHours(1));
        _mockCredential
            .Setup(c => c.GetTokenAsync(It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedToken);

        var testHandler = new TestHttpMessageHandler(HttpStatusCode.Created, "Test response");
        var handler = new BackupApiAuthHandler(_mockOptions.Object, _mockCredential.Object, _mockLogger.Object)
        {
            InnerHandler = testHandler
        };

        using var client = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/backup/commit");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        testHandler.CapturedRequest.Should().NotBeNull();
        testHandler.CapturedRequest!.Method.Should().Be(HttpMethod.Post);
        testHandler.CapturedRequest.RequestUri.Should().Be("https://api.example.com/backup/commit");
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Be("Test response");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SendAsync_ShouldPassCancellationTokenToCredential()
    {
        // Arrange
        var expectedToken = new AccessToken("token", DateTimeOffset.UtcNow.AddHours(1));
        var wasCalled = false;

        _mockCredential
            .Setup(c => c.GetTokenAsync(It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()))
            .Callback<TokenRequestContext, CancellationToken>((_, ct) =>
            {
                wasCalled = true;
                ct.CanBeCanceled.Should().BeTrue("cancellation token should be passed through");
            })
            .ReturnsAsync(expectedToken);

        var handler = new BackupApiAuthHandler(_mockOptions.Object, _mockCredential.Object, _mockLogger.Object)
        {
            InnerHandler = new TestHttpMessageHandler()
        };

        using var client = new HttpClient(handler);
        using var cts = new CancellationTokenSource();
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/test");

        // Act
        await client.SendAsync(request, cts.Token);

        // Assert
        wasCalled.Should().BeTrue("GetTokenAsync should have been called");
        _mockCredential.Verify(
            c => c.GetTokenAsync(It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SendAsync_WhenTokenCredentialThrows_ShouldPropagateException()
    {
        // Arrange
        var expectedException = new InvalidOperationException("Token acquisition failed");
        _mockCredential
            .Setup(c => c.GetTokenAsync(It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(expectedException);

        var handler = new BackupApiAuthHandler(_mockOptions.Object, _mockCredential.Object, _mockLogger.Object)
        {
            InnerHandler = new TestHttpMessageHandler()
        };

        using var client = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/test");

        // Act
        var act = async () => await client.SendAsync(request);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Token acquisition failed");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SendAsync_WithMultipleRequests_ShouldAcquireTokenForEach()
    {
        // Arrange
        var tokenCallCount = 0;
        _mockCredential
            .Setup(c => c.GetTokenAsync(It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                tokenCallCount++;
                return new AccessToken($"token-{tokenCallCount}", DateTimeOffset.UtcNow.AddHours(1));
            });

        var handler = new BackupApiAuthHandler(_mockOptions.Object, _mockCredential.Object, _mockLogger.Object)
        {
            InnerHandler = new TestHttpMessageHandler()
        };

        using var client = new HttpClient(handler);

        // Act
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/test1"));
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/test2"));
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/test3"));

        // Assert
        tokenCallCount.Should().Be(3, "should acquire token for each request");
        _mockCredential.Verify(
            c => c.GetTokenAsync(It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SendAsync_ShouldPreserveOriginalRequestHeaders()
    {
        // Arrange
        var expectedToken = new AccessToken("token", DateTimeOffset.UtcNow.AddHours(1));
        _mockCredential
            .Setup(c => c.GetTokenAsync(It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedToken);

        var testHandler = new TestHttpMessageHandler();
        var handler = new BackupApiAuthHandler(_mockOptions.Object, _mockCredential.Object, _mockLogger.Object)
        {
            InnerHandler = testHandler
        };

        using var client = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/test");
        request.Headers.Add("X-Custom-Header", "CustomValue");
        request.Headers.Add("X-Request-Id", "12345");

        // Act
        await client.SendAsync(request);

        // Assert
        testHandler.CapturedRequest.Should().NotBeNull();
        testHandler.CapturedRequest!.Headers.GetValues("X-Custom-Header").Should().ContainSingle()
            .Which.Should().Be("CustomValue");
        testHandler.CapturedRequest.Headers.GetValues("X-Request-Id").Should().ContainSingle()
            .Which.Should().Be("12345");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SendAsync_WhenCancelled_ShouldThrowOperationCanceledException()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        var expectedToken = new AccessToken("token", DateTimeOffset.UtcNow.AddHours(1));
        _mockCredential
            .Setup(c => c.GetTokenAsync(It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var handler = new BackupApiAuthHandler(_mockOptions.Object, _mockCredential.Object, _mockLogger.Object)
        {
            InnerHandler = new TestHttpMessageHandler()
        };

        using var client = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/test");

        // Act
        var act = async () => await client.SendAsync(request, cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SendAsync_ShouldReadOptionsFromSnapshot()
    {
        // Arrange
        var expectedToken = new AccessToken("token", DateTimeOffset.UtcNow.AddHours(1));
        _mockCredential
            .Setup(c => c.GetTokenAsync(It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedToken);

        var handler = new BackupApiAuthHandler(_mockOptions.Object, _mockCredential.Object, _mockLogger.Object)
        {
            InnerHandler = new TestHttpMessageHandler()
        };

        using var client = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/test");

        // Act
        await client.SendAsync(request);

        // Assert
        // Value is accessed twice: once for AuthenticationTenant, once for AuthenticationScope
        _mockOptions.Verify(o => o.Value, Times.AtLeastOnce(),
            "should read configuration from options snapshot");
    }

    /// <summary>
    /// Test HTTP message handler that captures the request and returns a configurable response.
    /// </summary>
    private class TestHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _responseContent;

        public HttpRequestMessage? CapturedRequest { get; private set; }

        public TestHttpMessageHandler(
            HttpStatusCode statusCode = HttpStatusCode.OK,
            string responseContent = "")
        {
            _statusCode = statusCode;
            _responseContent = responseContent;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CapturedRequest = request;

            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseContent)
            };

            return Task.FromResult(response);
        }
    }
}
