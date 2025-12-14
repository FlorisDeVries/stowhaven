using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FlorisDeV.BackupApi.Filters;

public class UnhandledExceptionHandler : ExceptionHandlerBase
{
    private readonly IHostEnvironment _environment;

    public UnhandledExceptionHandler(IHostEnvironment environment)
    {
        _environment = environment;
    }

    public override bool CanHandle(Exception exception) => true; // Handles all exceptions as fallback

    public override (int statusCode, ProblemDetails problemDetails) Handle(Exception exception, ExceptionContext context)
    {
        var detail = _environment.IsDevelopment()
            ? exception.Message
            : "An unexpected error occurred. Please try again later.";

        var problemDetails = CreateProblemDetails(
            context,
            StatusCodes.Status500InternalServerError,
            "Internal server error",
            detail
        );

        // Include stack trace in development mode
        if (_environment.IsDevelopment())
        {
            problemDetails.Extensions["stackTrace"] = exception.StackTrace;
            problemDetails.Extensions["exceptionType"] = exception.GetType().Name;
        }

        return (StatusCodes.Status500InternalServerError, problemDetails);
    }
}
