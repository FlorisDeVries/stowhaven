using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;

namespace FlorisDeV.BackupApi.Data;

public sealed class StateDocumentStoreOptions
{
    public const string SectionName = "Database";

    /// <summary>"Cosmos" or "Sqlite". Defaults to Sqlite in Development, Cosmos otherwise.</summary>
    public string? Provider { get; set; }

    public CosmosStoreOptions Cosmos { get; set; } = new();
    public SqliteStoreOptions Sqlite { get; set; } = new();

    public sealed class CosmosStoreOptions
    {
        public string? AccountEndpoint { get; set; }
        public string DatabaseName { get; set; } = "backup-state";
        public string ManifestContainerName { get; set; } = "manifest-state";
        public string DeviceRegistryContainerName { get; set; } = "device-registry";
    }

    public sealed class SqliteStoreOptions
    {
        public string FilePath { get; set; } = "run/data/backup-state.db";
    }
}

public static class StateDocumentStoreExtensions
{
    /// <summary>
    /// Serializer used for document payloads and for Cosmos envelope properties. Must stay
    /// consistent across providers so field filters compare identical representations.
    /// </summary>
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    extension(WebApplicationBuilder builder)
    {
        public void AddStateDocumentStores()
        {
            var options = builder.Configuration.GetSection(StateDocumentStoreOptions.SectionName)
                              .Get<StateDocumentStoreOptions>() ?? new StateDocumentStoreOptions();
            var provider = options.Provider
                           ?? (builder.Environment.IsDevelopment() ? "Sqlite" : "Cosmos");

            if (string.Equals(provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                // One shared database file: the API and worker are separate processes in
                // local development, exactly like they share one Cosmos database in Azure.
                builder.Services.AddSingleton<SqliteStateDocumentStore>(_ =>
                    new SqliteStateDocumentStore(options.Sqlite.FilePath, SerializerOptions));
                builder.Services.AddKeyedSingleton<IStateDocumentStore>(StateStores.Manifest,
                    (sp, _) => sp.GetRequiredService<SqliteStateDocumentStore>());
                builder.Services.AddKeyedSingleton<IStateDocumentStore>(StateStores.DeviceRegistry,
                    (sp, _) => sp.GetRequiredService<SqliteStateDocumentStore>());
                return;
            }

            if (!string.Equals(provider, "Cosmos", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Unknown Database:Provider '{provider}'. Expected 'Cosmos' or 'Sqlite'.");
            }

            var accountEndpoint = options.Cosmos.AccountEndpoint
                                  ?? throw new InvalidOperationException("Database:Cosmos:AccountEndpoint is required for the Cosmos provider.");

            builder.Services.AddSingleton(_ => new CosmosClient(
                accountEndpoint,
                new DefaultAzureCredential(),
                new CosmosClientOptions
                {
                    UseSystemTextJsonSerializerWithOptions = SerializerOptions
                }));

            builder.Services.AddKeyedSingleton<IStateDocumentStore>(StateStores.Manifest, (sp, _) =>
                new CosmosStateDocumentStore(
                    sp.GetRequiredService<CosmosClient>()
                        .GetContainer(options.Cosmos.DatabaseName, options.Cosmos.ManifestContainerName),
                    SerializerOptions));

            builder.Services.AddKeyedSingleton<IStateDocumentStore>(StateStores.DeviceRegistry, (sp, _) =>
                new CosmosStateDocumentStore(
                    sp.GetRequiredService<CosmosClient>()
                        .GetContainer(options.Cosmos.DatabaseName, options.Cosmos.DeviceRegistryContainerName),
                    SerializerOptions));
        }
    }
}
