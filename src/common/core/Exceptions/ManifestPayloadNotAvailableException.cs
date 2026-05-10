using FlorisDeV.Logging.ErrorHandling;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FlorisDeV.BackupApi.Exceptions;

public sealed class ManifestPayloadNotAvailableException(
    Guid deviceId,
    Guid runId
) : Exception($"Manifest payload for backup run '{runId}' on device '{deviceId}' is not available")
{
    public Guid DeviceId { get; } = deviceId;
    public Guid RunId { get; } = runId;
}

public sealed class ManifestPayloadNotAvailableExceptionHandler : ExceptionHandlerBase
{
    public override bool CanHandle(Exception exception) => exception is ManifestPayloadNotAvailableException;

    public override (int statusCode, ProblemDetails problemDetails) Handle(Exception exception, ExceptionContext context)
    {
        var manifestEx = (ManifestPayloadNotAvailableException)exception;
        var problemDetails = CreateProblemDetails(
            context,
            StatusCodes.Status404NotFound,
            "Manifest payload not available",
            manifestEx.Message);

        problemDetails.Extensions["deviceId"] = manifestEx.DeviceId;
        problemDetails.Extensions["runId"] = manifestEx.RunId;

        return (StatusCodes.Status404NotFound, problemDetails);
    }
}
