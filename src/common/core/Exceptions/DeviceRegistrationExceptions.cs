using FlorisDeV.Logging.ErrorHandling;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FlorisDeV.BackupApi.Exceptions;

public sealed class DeviceAlreadyRegisteredException(Guid deviceId)
    : Exception($"Device '{deviceId}' is already registered to another user")
{
    public Guid DeviceId { get; } = deviceId;
}

public sealed class DeviceNotRegisteredException(Guid deviceId)
    : Exception($"Device '{deviceId}' is not registered")
{
    public Guid DeviceId { get; } = deviceId;
}

public sealed class DeviceAccessDeniedException(Guid deviceId)
    : Exception($"The authenticated user is not allowed to access device '{deviceId}'")
{
    public Guid DeviceId { get; } = deviceId;
}

public sealed class UserIdentityRequiredException()
    : Exception("A delegated user token is required for this operation")
{
}

public sealed class DeviceAlreadyRegisteredExceptionHandler : ExceptionHandlerBase
{
    public override bool CanHandle(Exception exception) => exception is DeviceAlreadyRegisteredException;

    public override (int statusCode, ProblemDetails problemDetails) Handle(Exception exception, ExceptionContext context)
    {
        var deviceEx = (DeviceAlreadyRegisteredException)exception;
        var problemDetails = CreateProblemDetails(context, StatusCodes.Status409Conflict, "Device already registered", exception.Message);
        problemDetails.Extensions["deviceId"] = deviceEx.DeviceId;
        return (StatusCodes.Status409Conflict, problemDetails);
    }
}

public sealed class DeviceNotRegisteredExceptionHandler : ExceptionHandlerBase
{
    public override bool CanHandle(Exception exception) => exception is DeviceNotRegisteredException;

    public override (int statusCode, ProblemDetails problemDetails) Handle(Exception exception, ExceptionContext context)
    {
        var deviceEx = (DeviceNotRegisteredException)exception;
        var problemDetails = CreateProblemDetails(context, StatusCodes.Status404NotFound, "Device not registered", exception.Message);
        problemDetails.Extensions["deviceId"] = deviceEx.DeviceId;
        return (StatusCodes.Status404NotFound, problemDetails);
    }
}

public sealed class DeviceAccessDeniedExceptionHandler : ExceptionHandlerBase
{
    public override bool CanHandle(Exception exception) => exception is DeviceAccessDeniedException;

    public override (int statusCode, ProblemDetails problemDetails) Handle(Exception exception, ExceptionContext context)
    {
        var deviceEx = (DeviceAccessDeniedException)exception;
        var problemDetails = CreateProblemDetails(context, StatusCodes.Status403Forbidden, "Device access denied", exception.Message);
        problemDetails.Extensions["deviceId"] = deviceEx.DeviceId;
        return (StatusCodes.Status403Forbidden, problemDetails);
    }
}

public sealed class UserIdentityRequiredExceptionHandler : ExceptionHandlerBase
{
    public override bool CanHandle(Exception exception) => exception is UserIdentityRequiredException;

    public override (int statusCode, ProblemDetails problemDetails) Handle(Exception exception, ExceptionContext context)
    {
        var problemDetails = CreateProblemDetails(
            context,
            StatusCodes.Status403Forbidden,
            "User identity required",
            exception.Message);

        return (StatusCodes.Status403Forbidden, problemDetails);
    }
}