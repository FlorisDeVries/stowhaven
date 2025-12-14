using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FlorisDeV.BackupApi.Filters;

public class ArgumentExceptionHandler : ExceptionHandlerBase
{
    public override bool CanHandle(Exception exception) => exception is ArgumentException and not ArgumentNullException;

    public override (int statusCode, ProblemDetails problemDetails) Handle(Exception exception, ExceptionContext context)
    {
        var argEx = (ArgumentException)exception;
        
        var problemDetails = CreateProblemDetails(
            context,
            StatusCodes.Status400BadRequest,
            "Invalid argument",
            argEx.Message
        );

        if (!string.IsNullOrEmpty(argEx.ParamName))
            problemDetails.Extensions["paramName"] = argEx.ParamName;

        return (StatusCodes.Status400BadRequest, problemDetails);
    }
}
