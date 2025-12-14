namespace FlorisDeV.BackupApi.Filters;

/// <summary>
/// Extension methods for registering exception handlers.
/// </summary>
public static class ExceptionHandlerServiceCollectionExtensions
{
    public static IServiceCollection AddExceptionHandlers(this IServiceCollection services)
    {
        // Domain-specific exception handlers (co-located with exceptions)
        services.AddSingleton<IExceptionHandler, Exceptions.BackupRunNotFoundExceptionHandler>();
        services.AddSingleton<IExceptionHandler, Exceptions.BackupRunAlreadyCommittedExceptionHandler>();
        services.AddSingleton<IExceptionHandler, Exceptions.ConcurrentUpdateExceptionHandler>();
        services.AddSingleton<IExceptionHandler, Exceptions.InvalidBackupRunStateExceptionHandler>();
        services.AddSingleton<IExceptionHandler, Exceptions.SecretNotFoundExceptionHandler>();
        services.AddSingleton<IExceptionHandler, Exceptions.SecretStoreUnavailableExceptionHandler>();
        
        // General exception handlers
        services.AddSingleton<IExceptionHandler, ArgumentNullExceptionHandler>();
        services.AddSingleton<IExceptionHandler, ArgumentExceptionHandler>();
        services.AddSingleton<IExceptionHandler, UnhandledExceptionHandler>();

        return services;
    }
}
