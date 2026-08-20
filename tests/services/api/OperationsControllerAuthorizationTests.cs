using System.Security.Claims;
using FlorisDeV.BackupApi.Controllers;
using FlorisDeV.Security.Authorization;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;

namespace FlorisDeV.BackupApi.Tests;

public class OperationsControllerAuthorizationTests
{
    [Fact]
    public void ControllerRequiresBackupAdminPolicy()
    {
        var attribute = typeof(OperationsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Single();

        attribute.Policy.Should().Be(BackupAuthorizationPolicies.Admin);
    }

    [Theory]
    [InlineData("backup.admin", true)]
    [InlineData("backup.client backup.admin", true)]
    [InlineData("backup.client", false)]
    [InlineData("backup.administrator", false)]
    [InlineData("", false)]
    public void AdminScopeMatchingIsExact(string scopes, bool expected)
    {
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim("scp", scopes)], "Test"));

        BackupAuthorizationPolicies
            .HasScope(principal, BackupAuthorizationPolicies.AdminScope)
            .Should().Be(expected);
    }
}
