using System.Net;
using System.Text;
using FlorisDeV.BackupContracts.State;
using FlorisDeV.BackupMonitoring.Services;
using Xunit;

namespace FlorisDeV.BackupMonitoring.Tests;

public class MonitoringApiTests
{
    [Fact]
    public async Task CommitQueryPreservesOpaqueContinuationTokensAndUsesBoundedPageSize()
    {
        var handler = new RecordingHandler("""{"commits":[],"pageSize":25} """);
        var api = CreateApi(handler);
        const string token = "abc+/=&?opaque";
        await api.CommitsAsync(status: "Failed", continuation: token);

        Assert.Equal("/api/ops/commits", handler.RequestUri!.AbsolutePath);
        Assert.Contains("pageSize=25", handler.RequestUri.Query);
        Assert.Contains("status=Failed", handler.RequestUri.Query);
        Assert.Contains("continuationToken=" + Uri.EscapeDataString(token), handler.RequestUri.Query);
        Assert.Equal(HttpMethod.Get, handler.Method);
    }

    [Fact]
    public async Task InvestigationQueriesEncodeOwnerIdsPathsAndContinuationTokens()
    {
        var handler = new RecordingHandler("""{"devices":[],"tenantId":"tenant-a","pageSize":25}""");
        var api = CreateApi(handler);
        await api.DevicesAsync(userId: "owner+a&b", continuation: "opaque+/=&?");
        Assert.Equal("/api/ops/devices", handler.RequestUri!.AbsolutePath);
        Assert.Contains("userId=owner%2Ba%26b", handler.RequestUri.Query);
        Assert.Contains("continuationToken=opaque%2B%2F%3D%26%3F", handler.RequestUri.Query);
        Assert.Contains("pageSize=25", handler.RequestUri.Query);
    }

    [Fact]
    public async Task FileInspectionUsesAnEncodedExactLogicalPath()
    {
        var handler = new RecordingHandler("""{"deviceId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","entry":{"logicalPath":"home/a.txt","currentVersionId":"v1","size":1,"lastWriteUtc":"2026-01-01T00:00:00Z","lastBackupRunId":"run","isDeleted":false},"versionAvailable":false}""");
        const string logicalPath = "home/folder a&b/#file.txt";
        await CreateApi(handler).DeviceFileAsync(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), logicalPath, default);
        Assert.Contains("logicalPath=" + Uri.EscapeDataString(logicalPath), handler.RequestUri!.Query);
        Assert.Equal("", handler.RequestUri.Fragment);
        Assert.Equal(HttpMethod.Get, handler.Method);
    }

    [Theory]
    [InlineData("\"Processing\"")]
    [InlineData("1")]
    public async Task SupportsStringAndNumericApiEnums(string status)
    {
        var handler = new RecordingHandler($$"""{"commits":[{"status":{{status}}}],"pageSize":25} """);
        var result = await CreateApi(handler).CommitsAsync();
        Assert.Equal(CommitJobStatus.Processing, Assert.Single(result.Commits).Status);
    }

    [Fact]
    public async Task RetryUsesPostWithoutResendingManifestOrFileData()
    {
        var id = Guid.NewGuid();
        var handler = new RecordingHandler("{}");
        await CreateApi(handler).RetryAsync(id, CancellationToken.None);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal($"/api/ops/commits/{id}/retry", handler.RequestUri!.AbsolutePath);
        Assert.False(handler.HasContent);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "session")]
    [InlineData(HttpStatusCode.Forbidden, "allowlist")]
    [InlineData(HttpStatusCode.TooManyRequests, "busy")]
    public async Task ApiFailuresProvideActionableMessagesWithoutReflectingRawHtml(HttpStatusCode status, string expected)
    {
        var handler = new RecordingHandler("<script>untrusted proxy error</script>", status);
        var exception = await Assert.ThrowsAsync<MonitoringApiException>(() => CreateApi(handler).CommitsAsync());
        Assert.Equal(status, exception.StatusCode);
        Assert.Contains(expected, exception.Message);
        Assert.DoesNotContain("<script>", exception.Message);
    }

    private static MonitoringApi CreateApi(RecordingHandler handler) => new(new HttpClient(handler)
    {
        BaseAddress = new Uri("https://gateway.example.com/")
    });

    private sealed class RecordingHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public bool HasContent { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Method = request.Method;
            HasContent = request.Content is not null;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
