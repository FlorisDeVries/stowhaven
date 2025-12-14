using FlorisDeV.BackupApi.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FlorisDeV.BackupApi.Exceptions;

public class SecretStoreUnavailableException(
    string store,
    Exception inner
) : Exception($"Secret store '{store}' is unavailable.", inner)
{
    public string SecretStore { get; } = store;
}

public class SecretStoreUnavailableExceptionHandler : ExceptionHandlerBase
{
    public override bool CanHandle(Exception exception) => exception is SecretStoreUnavailableException;

    public override (int statusCode, ProblemDetails problemDetails) Handle(Exception exception, ExceptionContext context)
    {
        var secretStoreUnavailableEx = (SecretStoreUnavailableException)exception;
        
        var problemDetails = CreateProblemDetails(
            context,
            StatusCodes.Status503ServiceUnavailable,
            "Secret store unavailable",
            secretStoreUnavailableEx.Message
        );

        problemDetails.Extensions["secretStore"] = secretStoreUnavailableEx.SecretStore;

        return (StatusCodes.Status503ServiceUnavailable, problemDetails);
    }
}