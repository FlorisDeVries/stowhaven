using FlorisDeV.BackupMonitoring.Services;
using Xunit;

namespace FlorisDeV.BackupMonitoring.Tests;

public class MonitoringOptionsTests
{
    [Theory]
    [InlineData(true, "http://localhost:8200", true)]
    [InlineData(false, "http://localhost:8200", false)]
    [InlineData(true, "https://gateway.example.com", false)]
    public void LocalAuthenticationRequiresDevelopmentAndLoopback(bool development, string gateway, bool expected)
    {
        var options = new MonitoringOptions { GatewayBaseUrl = gateway, UseDevelopmentAuthentication = true };
        options.Validate(development);
        Assert.Equal(expected, options.UseLocalAuthentication);
        Assert.Equal(expected, options.IsConfigured);
    }

    [Theory]
    [InlineData("http://gateway.example.com")]
    [InlineData("https://gateway.example.com/api")]
    [InlineData("https://user:password@gateway.example.com")]
    [InlineData("https://gateway.example.com?token=secret")]
    public void ProductionRejectsNonHttpsOrNonOriginGatewayUrls(string gateway)
    {
        var options = ProductionOptions(gateway);
        options.Validate(false);
        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void ProductionConfigurationAcceptsPublicIdentifiersWithoutSecrets()
    {
        var options = ProductionOptions("https://gateway.example.com");
        options.Validate(false);
        Assert.True(options.IsConfigured);
        Assert.False(options.UseLocalAuthentication);
    }

    private static MonitoringOptions ProductionOptions(string gateway) => new()
    {
        GatewayBaseUrl = gateway,
        TenantId = Guid.NewGuid().ToString(),
        ClientId = Guid.NewGuid().ToString(),
        GatewayScope = $"api://{Guid.NewGuid()}/monitoring.admin"
    };
}
