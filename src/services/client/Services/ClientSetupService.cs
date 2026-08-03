using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using FlorisDeV.BackupClient.Clients.BackupApi;
using FlorisDeV.BackupClient.Clients.BackupApi.Config;
using FlorisDeV.BackupContracts.Api.Requests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlorisDeV.BackupClient.Services;

public interface IClientSetupService
{
    /// <summary>
    /// Interactive first-time setup: collects backup targets, signs in, and verifies
    /// the account can actually reach the backup API end-to-end. Individual steps can
    /// be skipped via <paramref name="options"/> for reruns that only need part of the flow.
    /// </summary>
    Task ConfigureAsync(ConfigureOptions options, CancellationToken cancellationToken);

    /// <summary>
    /// Acquires (or silently refreshes) an access token without any other side effects.
    /// </summary>
    Task LoginAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Controls which steps of the "configure" flow run. All steps run by default.
/// </summary>
public sealed record ConfigureOptions(
    bool SkipTargets = false,
    bool SkipLogin = false,
    bool SkipAccessCheck = false);

public partial class ClientSetupService(
    IConfiguration configuration,
    IOptions<BackupApiClientOptions> apiOptions,
    TokenCredential credential,
    IBackupApiClient backupApiClient,
    IBackupStateService backupStateService,
    ILogger<ClientSetupService> logger) : IClientSetupService
{
    private static readonly string LocalConfigPath = Path.Combine(AppContext.BaseDirectory, "appsettings.local.json");

    public async Task ConfigureAsync(ConfigureOptions options, CancellationToken cancellationToken)
    {
        Console.WriteLine("=== Backup Client Setup ===");
        Console.WriteLine();

        if (options.SkipTargets)
        {
            Console.WriteLine("Skipping backup target setup (--skip-targets).");
        }
        else
        {
            ConfigureBackupTargets();
        }

        if (options.SkipLogin)
        {
            Console.WriteLine();
            Console.WriteLine("Skipping login (--skip-login).");
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("Signing in...");
            await LoginAsync(cancellationToken);
        }

        if (options.SkipAccessCheck)
        {
            Console.WriteLine();
            Console.WriteLine("Skipping access check (--skip-access-check).");
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("Checking access to the backup API...");
            await CheckAccessAsync(cancellationToken);
        }

        Console.WriteLine();
        Console.WriteLine("Setup complete.");
    }

    public async Task LoginAsync(CancellationToken cancellationToken)
    {
        var options = apiOptions.Value;
        LogSigningIn(options.ApiUrl);

        var tokenRequest = new TokenRequestContext([options.AuthenticationScope], tenantId: options.AuthenticationTenant);
        var token = await credential.GetTokenAsync(tokenRequest, cancellationToken);

        LogLoginSucceeded(token.ExpiresOn);
    }

    private async Task CheckAccessAsync(CancellationToken cancellationToken)
    {
        try
        {
            var deviceState = await backupStateService.GetOrCreateDeviceStateAsync(cancellationToken);
            var response = await backupApiClient.RegisterDevice(new RegisterDeviceRequest
            {
                DeviceId = deviceState.DeviceId,
                DisplayName = Environment.MachineName
            }, cancellationToken);

            Console.WriteLine($"Access OK - registered as device {response.DeviceId} ({response.DisplayName}).");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Access check failed: {ex.Message}");
            Console.WriteLine("Verify the account has been granted access (see doc/AUTHENTICATION.md) and re-run 'login' once fixed.");
            throw;
        }
    }

    private void ConfigureBackupTargets()
    {
        var targets = ReadCurrentBackupTargets();

        Console.WriteLine(targets.Count > 0
            ? $"Currently configured backup targets ({targets.Count}):"
            : "No backup targets are configured yet.");

        foreach (var (name, path) in targets)
        {
            Console.WriteLine($"  - {name}: {path}");
        }

        OfferSuggestedTargets(targets);

        Console.WriteLine();
        Console.WriteLine("Add a backup target (leave the name blank to finish):");

        while (true)
        {
            Console.Write("  Target name: ");
            var name = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(name))
                break;

            if (name.Contains('/') || name.Contains('\\'))
            {
                Console.WriteLine("  Target name cannot contain slashes. Try again.");
                continue;
            }

            Console.Write("  Folder path: ");
            var path = Console.ReadLine()?.Trim().Trim('"');

            if (string.IsNullOrEmpty(path))
            {
                Console.WriteLine("  Path cannot be empty. Try again.");
                continue;
            }

            var validation = BackupValidator.ValidateBackupDirectory(path);
            if (validation.Severity == ValidationSeverity.Error)
            {
                Console.WriteLine($"  {validation.Message}");
                continue;
            }

            if (validation.Severity == ValidationSeverity.Warning)
            {
                Console.WriteLine($"  Warning: {validation.Message}");
            }

            targets[name] = Path.GetFullPath(path);
            Console.WriteLine($"  Added '{name}' -> {targets[name]}");
            Console.WriteLine();
        }

        if (targets.Count == 0)
        {
            Console.WriteLine("No backup targets configured; nothing to save.");
            return;
        }

        WriteBackupTargets(LocalConfigPath, targets);
        Console.WriteLine($"Saved {targets.Count} backup target(s) to {LocalConfigPath}");
    }

    private void OfferSuggestedTargets(Dictionary<string, string> targets)
    {
        var suggestions = GetSuggestedTargets()
            .Where(s => !targets.ContainsKey(s.Name))
            .ToList();

        if (suggestions.Count == 0)
            return;

        Console.WriteLine();
        Console.WriteLine("Detected common folders on this machine:");
        for (var i = 0; i < suggestions.Count; i++)
        {
            Console.WriteLine($"  {i + 1}. {suggestions[i].Name} -> {suggestions[i].Path}");
        }

        Console.Write("Enter numbers to add (comma-separated), or press Enter to skip: ");
        var selection = Console.ReadLine();

        foreach (var index in ParseSelection(selection, suggestions.Count))
        {
            var (name, path) = suggestions[index];
            targets[name] = path;
            Console.WriteLine($"  Added '{name}' -> {path}");
        }
    }

    /// <summary>
    /// Detects common user folders (Documents, Pictures, Desktop, etc.) that actually exist on
    /// this machine, so the wizard can suggest them instead of requiring a typed-out path.
    /// </summary>
    internal static IReadOnlyList<(string Name, string Path)> GetSuggestedTargets()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
            return [];

        var candidates = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new (string Name, string Path)[]
            {
                ("documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
                ("pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
                ("videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
                ("music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
                ("desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
                ("downloads", Path.Combine(userProfile, "Downloads"))
            }
            : new (string Name, string Path)[]
            {
                ("documents", Path.Combine(userProfile, "Documents")),
                ("pictures", Path.Combine(userProfile, "Pictures")),
                ("videos", Path.Combine(userProfile, "Videos")),
                ("music", Path.Combine(userProfile, "Music")),
                ("desktop", Path.Combine(userProfile, "Desktop")),
                ("downloads", Path.Combine(userProfile, "Downloads"))
            };

        return candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.Path) && Directory.Exists(c.Path))
            .Select(c => (c.Name, Path: Path.GetFullPath(c.Path)))
            .ToList();
    }

    /// <summary>
    /// Parses a comma-separated list of 1-based selection numbers, silently ignoring blanks,
    /// out-of-range values, and anything that doesn't parse as an integer.
    /// </summary>
    internal static IEnumerable<int> ParseSelection(string? input, int count)
    {
        if (string.IsNullOrWhiteSpace(input))
            yield break;

        foreach (var token in input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(token, out var number) && number >= 1 && number <= count)
            {
                yield return number - 1;
            }
        }
    }

    private Dictionary<string, string> ReadCurrentBackupTargets() => ParseBackupTargets(configuration);

    /// <summary>
    /// Reads BackupClient:BackupTargets directly from raw configuration (not the bound/validated
    /// BackupClientOptions), since that options type may be legitimately empty before setup runs.
    /// </summary>
    internal static Dictionary<string, string> ParseBackupTargets(IConfiguration configuration)
    {
        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var child in configuration.GetSection("BackupClient:BackupTargets").GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(child.Value))
            {
                targets[child.Key] = child.Value;
            }
        }

        return targets;
    }

    /// <summary>
    /// Merges the given backup targets into BackupClient:BackupTargets in the local config file,
    /// preserving any other content already there.
    /// </summary>
    internal static void WriteBackupTargets(string localConfigPath, IReadOnlyDictionary<string, string> targets)
    {
        var root = File.Exists(localConfigPath) && JsonNode.Parse(File.ReadAllText(localConfigPath)) is JsonObject existing
            ? existing
            : new JsonObject();

        if (root["BackupClient"] is not JsonObject backupClientNode)
        {
            backupClientNode = new JsonObject();
            root["BackupClient"] = backupClientNode;
        }

        var targetsNode = new JsonObject();
        foreach (var (name, path) in targets.OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase))
        {
            targetsNode[name] = path;
        }

        backupClientNode["BackupTargets"] = targetsNode;

        File.WriteAllText(localConfigPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    [LoggerMessage(LogLevel.Information, "Signing in for {ApiUrl}...")]
    partial void LogSigningIn(string apiUrl);

    [LoggerMessage(LogLevel.Information, "Login succeeded; token cached and valid until {ExpiresOn}.")]
    partial void LogLoginSucceeded(DateTimeOffset expiresOn);
}
