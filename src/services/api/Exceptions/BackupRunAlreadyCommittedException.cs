using FlorisDeV.BackupApi.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FlorisDeV.BackupApi.Exceptions;

/// <summary>
/// Exception thrown when attempting to commit a backup run that has already been committed.
/// </summary>
public class BackupRunAlreadyCommittedException(
    Guid deviceId,
    Guid runId
) : Exception($"Backup run '{runId}' for device '{deviceId}' has already been committed")
{
    public Guid DeviceId { get; } = deviceId;
    public Guid RunId { get; } = runId;
}

public class BackupRunAlreadyCommittedExceptionHandler : ExceptionHandlerBase
{
    public override bool CanHandle(Exception exception) => exception is BackupRunAlreadyCommittedException;

    public override (int statusCode, ProblemDetails problemDetails) Handle(Exception exception, ExceptionContext context)
    {
        var alreadyCommittedEx = (BackupRunAlreadyCommittedException)exception;
        
        var problemDetails = CreateProblemDetails(
            context,
            StatusCodes.Status409Conflict,
            "Backup run already committed",
            alreadyCommittedEx.Message
        );

        problemDetails.Extensions["deviceId"] = alreadyCommittedEx.DeviceId;
        problemDetails.Extensions["runId"] = alreadyCommittedEx.RunId;

        return (StatusCodes.Status409Conflict, problemDetails);
    }
}