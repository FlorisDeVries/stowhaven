using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FlorisDeV.BackupApi.Filters;

/// <summary>
/// Interface for handling specific exception types and converting them to ProblemDetails responses.
/// </summary>
public interface IExceptionHandler
{
    /// <summary>
    /// Determines if this handler can handle the given exception.
    /// </summary>
    bool CanHandle(Exception exception);

    /// <summary>
    /// Handles the exception and returns the appropriate status code and ProblemDetails.
    /// </summary>
    (int statusCode, ProblemDetails problemDetails) Handle(Exception exception, ExceptionContext context);
}
