using FlorisDeV.BackupApi.Exceptions;
using FlorisDeV.Logging.ErrorHandling;

namespace FlorisDeV.BackupApi.Filters;

/// <summary>
/// Extension methods for registering exception handlers.
/// </summary>
public static class ExceptionHandlerServiceCollectionExtensions
{
    public static IServiceCollection AddExceptionHandlers(this IServiceCollection services)
    {
        // Domain-specific exception handlers (co-located with exceptions)
        services.AddSingleton<IExceptionHandler, BackupRunNotFoundExceptionHandler>();
        services.AddSingleton<IExceptionHandler, BackupRunAlreadyCommittedExceptionHandler>();
        services.AddSingleton<IExceptionHandler, ConcurrentUpdateExceptionHandler>();
        services.AddSingleton<IExceptionHandler, InvalidBackupRunStateExceptionHandler>();
        services.AddSingleton<IExceptionHandler, SecretNotFoundExceptionHandler>();
        services.AddSingleton<IExceptionHandler, SecretStoreUnavailableExceptionHandler>();
        services.AddSingleton<IExceptionHandler, DeviceAlreadyRegisteredExceptionHandler>();
        services.AddSingleton<IExceptionHandler, DeviceNotRegisteredExceptionHandler>();
        services.AddSingleton<IExceptionHandler, DeviceAccessDeniedExceptionHandler>();
        
        // General exception handlers
        services.AddSingleton<IExceptionHandler, ArgumentNullExceptionHandler>();
        services.AddSingleton<IExceptionHandler, ArgumentExceptionHandler>();
        services.AddSingleton<IExceptionHandler, UnhandledExceptionHandler>();

        return services;
    }
}
