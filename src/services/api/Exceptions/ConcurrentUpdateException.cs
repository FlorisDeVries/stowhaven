using FlorisDeV.BackupApi.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FlorisDeV.BackupApi.Exceptions;

/// <summary>
/// Exception thrown when a concurrent update conflict is detected (ETag mismatch).
/// This indicates that the resource was modified by another request between the read and write operations.
/// </summary>
public class ConcurrentUpdateException(
    Guid deviceId,
    Guid runId,
    string? expectedETag,
    string? actualETag
) : Exception($"Concurrent update detected for backup run '{runId}' of device '{deviceId}'. " +
              $"The resource has been modified by another request. Please retry the operation.")
{
    public Guid DeviceId { get; } = deviceId;
    public Guid RunId { get; } = runId;
    public string? ExpectedETag { get; } = expectedETag;
    public string? ActualETag { get; } = actualETag;
}

public class ConcurrentUpdateExceptionHandler : ExceptionHandlerBase
{
    public override bool CanHandle(Exception exception) => exception is ConcurrentUpdateException;

    public override (int statusCode, ProblemDetails problemDetails) Handle(Exception exception, ExceptionContext context)
    {
        var concurrencyEx = (ConcurrentUpdateException)exception;
        
        var problemDetails = CreateProblemDetails(
            context,
            StatusCodes.Status409Conflict,
            "Concurrent update conflict",
            concurrencyEx.Message
        );

        problemDetails.Extensions["deviceId"] = concurrencyEx.DeviceId;
        problemDetails.Extensions["runId"] = concurrencyEx.RunId;

        if (!string.IsNullOrEmpty(concurrencyEx.ExpectedETag))
            problemDetails.Extensions["expectedETag"] = concurrencyEx.ExpectedETag;

        if (!string.IsNullOrEmpty(concurrencyEx.ActualETag))
            problemDetails.Extensions["actualETag"] = concurrencyEx.ActualETag;

        return (StatusCodes.Status409Conflict, problemDetails);
    }
}