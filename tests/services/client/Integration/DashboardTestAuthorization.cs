using System.Net.Http.Json;
using System.Text.Json;
using FlorisDeV.BackupClient.Dashboard;

namespace FlorisDeV.BackupClient.Tests.Integration;

internal static class DashboardTestAuthorization
{
    internal static async Task AuthorizeAsync(HttpClient browser, DashboardOptions options)
    {
        var url = await DashboardCompanion.OpenAsync(options);
        using var response = await browser.PostAsJsonAsync("api/session", new { ticket = url.Fragment[1..] });
        response.EnsureSuccessStatusCode();
        var grant = await response.Content.ReadFromJsonAsync<JsonElement>();
        browser.DefaultRequestHeaders.Authorization = new("Bearer", grant.GetProperty("session").GetString());
    }
}
