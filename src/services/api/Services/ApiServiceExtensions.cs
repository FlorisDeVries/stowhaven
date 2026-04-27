using FlorisDeV.BackupApi.Services;
using FlorisDeV.BackupApi.Options;

namespace FlorisDeV.BackupApi;

public static class ApiServiceExtensions
{
    extension(WebApplicationBuilder builder)
    {
        public void AddBackupApiServices()
        {
            builder.Services.Configure<SasSecurityOptions>(builder.Configuration.GetSection(SasSecurityOptions.SectionName));
            builder.Services.AddScoped<ISasUrlService, SasUrlService>();
            builder.Services.AddScoped<IBackupRunService, BackupRunService>();
            builder.Services.AddScoped<IBackupEventPublisher, BackupEventPublisher>();
            builder.Services.AddScoped<IRestoreService, RestoreService>();
            builder.Services.AddScoped<IDeviceRegistryService, DeviceRegistryService>();
            builder.Services.AddScoped<IDeviceAuthorizationService, DeviceAuthorizationService>();
        }
    }
}
