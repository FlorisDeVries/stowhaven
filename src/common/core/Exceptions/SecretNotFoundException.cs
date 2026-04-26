using FlorisDeV.Logging.ErrorHandling;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FlorisDeV.BackupApi.Exceptions;

/// <summary>
/// Represents an exception which is thrown when a required secret cannot be found
/// in the specified secret store.
/// </summary>
public class SecretNotFoundException(
    string store,
    string name
) : Exception($"Required secret '{name}' was not found in secret store '{store}'.")
{
    public string SecretName { get; } = name;
    public string SecretStore { get; } = store;
}

public class SecretNotFoundExceptionHandler : ExceptionHandlerBase
{
    public override bool CanHandle(Exception exception) => exception is SecretNotFoundException;

    public override (int statusCode, ProblemDetails problemDetails) Handle(Exception exception, ExceptionContext context)
    {
        var secretNotFoundEx = (SecretNotFoundException)exception;
        
        var problemDetails = CreateProblemDetails(
            context,
            StatusCodes.Status500InternalServerError,
            "Secret not found",
            secretNotFoundEx.Message
        );

        problemDetails.Extensions["secretStore"] = secretNotFoundEx.SecretStore;
        problemDetails.Extensions["secretName"] = secretNotFoundEx.SecretName;

        return (StatusCodes.Status500InternalServerError, problemDetails);
    }
}