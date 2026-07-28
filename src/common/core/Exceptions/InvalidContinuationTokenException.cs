using FlorisDeV.Logging.ErrorHandling;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FlorisDeV.BackupApi.Exceptions;

/// <summary>
/// Raised when a caller supplies a continuation token that cannot be decoded. Tokens are opaque, so a
/// malformed one is a client mistake rather than a server fault.
/// </summary>
public sealed class InvalidContinuationTokenException()
    : Exception("The supplied continuation token is not valid. Omit it to start from the first page.");

public sealed class InvalidContinuationTokenExceptionHandler : ExceptionHandlerBase
{
    public override bool CanHandle(Exception exception) => exception is InvalidContinuationTokenException;

    public override (int statusCode, ProblemDetails problemDetails) Handle(Exception exception, ExceptionContext context)
    {
        var problemDetails = CreateProblemDetails(
            context,
            StatusCodes.Status400BadRequest,
            "Invalid continuation token",
            exception.Message);

        return (StatusCodes.Status400BadRequest, problemDetails);
    }
}
