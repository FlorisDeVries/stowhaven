using Azure.Core;
using Azure.Identity;
using FluentAssertions;
using FlorisDeV.BackupClient.Authentication;
using Microsoft.Identity.Client;

namespace FlorisDeV.BackupClient.Tests.Authentication;

public class MsalTokenCredentialTests
{
    private const string Scope = "api://backup/backup.access";

    private readonly StubMsalTokenClient _client = new();

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetTokenAsync_SilentOnlyWithoutCachedAccount_RequiresLoginWithoutInteraction()
    {
        _client.Accounts = [];
        var credential = CreateCredential(allowInteractiveAuthentication: false);

        var act = async () => await credential.GetTokenAsync(CreateRequest(), CancellationToken.None);

        await act.Should().ThrowAsync<AuthenticationFailedException>()
            .WithMessage("*backup-client login*");
        _client.InteractiveCallCount.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetTokenAsync_SilentOnlyWithValidCache_ReturnsTokenWithoutInteraction()
    {
        var account = Moq.Mock.Of<IAccount>();
        var expected = new AccessToken("silent-token", DateTimeOffset.UtcNow.AddHours(1));
        _client.Accounts = [account];
        _client.AcquireSilent = (scopes, actualAccount, _) =>
        {
            scopes.Should().Equal(Scope);
            actualAccount.Should().BeSameAs(account);
            return Task.FromResult(expected);
        };
        var credential = CreateCredential(allowInteractiveAuthentication: false);

        var result = await credential.GetTokenAsync(CreateRequest(), CancellationToken.None);

        result.Should().Be(expected);
        _client.InteractiveCallCount.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetTokenAsync_SilentOnlyWhenMsalRequiresUi_RequiresLoginWithoutInteraction()
    {
        var account = Moq.Mock.Of<IAccount>();
        _client.Accounts = [account];
        _client.AcquireSilent = (_, _, _) => Task.FromException<AccessToken>(
            new MsalUiRequiredException("interaction_required", "User interaction is required"));
        var credential = CreateCredential(allowInteractiveAuthentication: false);

        var act = async () => await credential.GetTokenAsync(CreateRequest(), CancellationToken.None);

        await act.Should().ThrowAsync<AuthenticationFailedException>()
            .WithMessage("*backup-client login*");
        _client.InteractiveCallCount.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetTokenAsync_InteractiveCommandWithoutCachedAccount_SignsInInteractively()
    {
        var expected = new AccessToken("interactive-token", DateTimeOffset.UtcNow.AddHours(1));
        _client.Accounts = [];
        _client.AcquireInteractive = (scopes, _) =>
        {
            scopes.Should().Equal(Scope);
            return Task.FromResult(expected);
        };
        var credential = CreateCredential(allowInteractiveAuthentication: true);

        var result = await credential.GetTokenAsync(CreateRequest(), CancellationToken.None);

        result.Should().Be(expected);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetTokenAsync_InteractiveCommandWhenSilentRequiresUi_SignsInInteractively()
    {
        var account = Moq.Mock.Of<IAccount>();
        var expected = new AccessToken("interactive-token", DateTimeOffset.UtcNow.AddHours(1));
        _client.Accounts = [account];
        _client.AcquireSilent = (_, _, _) => Task.FromException<AccessToken>(
            new MsalUiRequiredException("interaction_required", "User interaction is required"));
        _client.AcquireInteractive = (_, _) => Task.FromResult(expected);
        var credential = CreateCredential(allowInteractiveAuthentication: true);

        var result = await credential.GetTokenAsync(CreateRequest(), CancellationToken.None);

        result.Should().Be(expected);
    }

    private MsalTokenCredential CreateCredential(bool allowInteractiveAuthentication)
        => new(_client, [Scope], allowInteractiveAuthentication);

    private static TokenRequestContext CreateRequest() => new([Scope]);

    private sealed class StubMsalTokenClient : IMsalTokenClient
    {
        public IReadOnlyList<IAccount> Accounts { get; set; } = [];

        public Func<string[], IAccount, CancellationToken, Task<AccessToken>> AcquireSilent { get; set; } =
            (_, _, _) => Task.FromException<AccessToken>(new InvalidOperationException("Unexpected silent token request"));

        public Func<string[], CancellationToken, Task<AccessToken>> AcquireInteractive { get; set; } =
            (_, _) => Task.FromException<AccessToken>(new InvalidOperationException("Unexpected interactive token request"));

        public int InteractiveCallCount { get; private set; }

        public Task<IReadOnlyList<IAccount>> GetAccountsAsync() => Task.FromResult(Accounts);

        public Task<AccessToken> AcquireTokenSilentAsync(
            string[] scopes,
            IAccount account,
            CancellationToken cancellationToken)
            => AcquireSilent(scopes, account, cancellationToken);

        public Task<AccessToken> AcquireTokenInteractiveAsync(
            string[] scopes,
            CancellationToken cancellationToken)
        {
            InteractiveCallCount++;
            return AcquireInteractive(scopes, cancellationToken);
        }
    }
}
