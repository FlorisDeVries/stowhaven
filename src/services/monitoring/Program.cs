using FlorisDeV.BackupMonitoring;
using FlorisDeV.BackupMonitoring.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var options = new MonitoringOptions();
builder.Configuration.GetSection("Monitoring").Bind(options);
options.Validate(builder.HostEnvironment.IsDevelopment());
builder.Services.AddSingleton(options);
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddMsalAuthentication(authentication =>
{
    authentication.ProviderOptions.Authentication.Authority =
        $"https://login.microsoftonline.com/{(Guid.TryParse(options.TenantId, out _) ? options.TenantId : "organizations")}";
    authentication.ProviderOptions.Authentication.ClientId =
        Guid.TryParse(options.ClientId, out _) ? options.ClientId : "00000000-0000-0000-0000-000000000000";
    authentication.ProviderOptions.Authentication.ValidateAuthority = true;
    authentication.ProviderOptions.LoginMode = "redirect";
    if (!string.IsNullOrWhiteSpace(options.GatewayScope))
        authentication.ProviderOptions.DefaultAccessTokenScopes.Add(options.GatewayScope);
});

if (options.UseLocalAuthentication)
    builder.Services.AddScoped<AuthenticationStateProvider, LocalAuthenticationStateProvider>();

builder.Services.AddScoped(services =>
{
    HttpMessageHandler handler = new HttpClientHandler();
    if (options.IsConfigured && !options.UseLocalAuthentication)
    {
        var authorization = services.GetRequiredService<AuthorizationMessageHandler>()
            .ConfigureHandler([options.GatewayBaseUrl], [options.GatewayScope]);
        authorization.InnerHandler = handler;
        handler = authorization;
    }
    return new MonitoringApi(new HttpClient(handler)
    {
        BaseAddress = new Uri(options.IsConfigured ? options.GatewayBaseUrl.TrimEnd('/') + "/" : builder.HostEnvironment.BaseAddress),
        Timeout = TimeSpan.FromSeconds(90)
    });
});

await builder.Build().RunAsync();
