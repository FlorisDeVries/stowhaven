namespace FlorisDeV.BackupMonitoring.Services;

public sealed class MonitoringOptions
{
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string GatewayBaseUrl { get; set; } = "";
    public string GatewayScope { get; set; } = "";
    public bool UseDevelopmentAuthentication { get; set; }
    public bool UseLocalAuthentication { get; private set; }
    public bool IsConfigured => Errors.Count == 0;
    public List<string> Errors { get; } = [];

    public void Validate(bool isDevelopment)
    {
        Errors.Clear();
        var validGateway = Uri.TryCreate(GatewayBaseUrl, UriKind.Absolute, out var gateway)
            && (gateway.Scheme == "https" || (isDevelopment && gateway.Scheme == "http" && gateway.IsLoopback))
            && gateway.AbsolutePath == "/" && string.IsNullOrEmpty(gateway.Query)
            && string.IsNullOrEmpty(gateway.Fragment) && string.IsNullOrEmpty(gateway.UserInfo);
        if (!validGateway)
            Errors.Add("Set Monitoring:GatewayBaseUrl to the HTTPS origin of your Stowhaven gateway.");

        UseLocalAuthentication = isDevelopment && validGateway && gateway!.IsLoopback && UseDevelopmentAuthentication;
        if (UseDevelopmentAuthentication && !UseLocalAuthentication)
            Errors.Add("Development authentication is only supported in Development with a loopback gateway.");
        if (UseLocalAuthentication) return;

        if (!Guid.TryParse(TenantId, out var tenant) || tenant == Guid.Empty)
            Errors.Add("Set Monitoring:TenantId to your Entra tenant ID.");
        if (!Guid.TryParse(ClientId, out var client) || client == Guid.Empty)
            Errors.Add("Set Monitoring:ClientId to the monitoring SPA's Entra application ID.");
        if (!GatewayScope.StartsWith("api://", StringComparison.OrdinalIgnoreCase)
            || !GatewayScope.EndsWith("/monitoring.admin", StringComparison.Ordinal))
            Errors.Add("Set Monitoring:GatewayScope to api://<gateway-client-id>/monitoring.admin.");
    }
}
