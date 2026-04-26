using FlorisDeV.Logging.ErrorHandling;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FlorisDeV.BackupApi.Exceptions;

public class BackupRunNotFoundException(
    Guid deviceId,
    Guid runId
) : Exception($"Backup run '{runId}' not found for device '{deviceId}'")
{
    public Guid DeviceId { get; } = deviceId;
    public Guid RunId { get; } = runId;
}

public class BackupRunNotFoundExceptionHandler : ExceptionHandlerBase
{
    public override bool CanHandle(Exception exception) => exception is BackupRunNotFoundException;

    public override (int statusCode, ProblemDetails problemDetails) Handle(Exception exception, ExceptionContext context)
    {
        var notFoundEx = (BackupRunNotFoundException)exception;
        
        var problemDetails = CreateProblemDetails(
            context,
            StatusCodes.Status404NotFound,
            "Backup run not found",
            notFoundEx.Message
        );

        problemDetails.Extensions["deviceId"] = notFoundEx.DeviceId;
        problemDetails.Extensions["runId"] = notFoundEx.RunId;

        return (StatusCodes.Status404NotFound, problemDetails);
    }
}