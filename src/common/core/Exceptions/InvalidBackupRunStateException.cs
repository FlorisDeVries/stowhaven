using FlorisDeV.Logging.ErrorHandling;
using FlorisDeV.BackupContracts.State;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FlorisDeV.BackupApi.Exceptions;

/// <summary>
/// Exception thrown when a backup run operation cannot be performed due to invalid state.
/// For example, trying to commit a run that is already in Failed state.
/// </summary>
public class InvalidBackupRunStateException(
    Guid deviceId,
    Guid runId,
    BackupRunStatus currentStatus,
    BackupRunStatus expectedStatus)
    : Exception($"Backup run '{runId}' for device '{deviceId}' is in '{currentStatus}' state. " +
                $"Expected state: '{expectedStatus}'")
{
    public Guid DeviceId { get; } = deviceId;
    public Guid RunId { get; } = runId;
    public BackupRunStatus CurrentStatus { get; } = currentStatus;
    public BackupRunStatus ExpectedStatus { get; } = expectedStatus;
}

public class InvalidBackupRunStateExceptionHandler : ExceptionHandlerBase
{
    public override bool CanHandle(Exception exception) => exception is InvalidBackupRunStateException;

    public override (int statusCode, ProblemDetails problemDetails) Handle(Exception exception, ExceptionContext context)
    {
        var invalidStateEx = (InvalidBackupRunStateException)exception;
        
        var problemDetails = CreateProblemDetails(
            context,
            StatusCodes.Status422UnprocessableEntity,
            "Invalid backup run state",
            invalidStateEx.Message
        );

        problemDetails.Extensions["deviceId"] = invalidStateEx.DeviceId;
        problemDetails.Extensions["runId"] = invalidStateEx.RunId;
        problemDetails.Extensions["currentStatus"] = invalidStateEx.CurrentStatus.ToString();
        problemDetails.Extensions["expectedStatus"] = invalidStateEx.ExpectedStatus.ToString();

        return (StatusCodes.Status422UnprocessableEntity, problemDetails);
    }
}
