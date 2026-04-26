using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FlorisDeV.Logging.ErrorHandling;

public class ArgumentNullExceptionHandler : ExceptionHandlerBase
{
    public override bool CanHandle(Exception exception) => exception is ArgumentNullException;

    public override (int statusCode, ProblemDetails problemDetails) Handle(Exception exception, ExceptionContext context)
    {
        var argNullEx = (ArgumentNullException)exception;
        
        var problemDetails = CreateProblemDetails(
            context,
            StatusCodes.Status400BadRequest,
            "Required argument is null",
            argNullEx.Message
        );

        if (!string.IsNullOrEmpty(argNullEx.ParamName))
            problemDetails.Extensions["paramName"] = argNullEx.ParamName;

        return (StatusCodes.Status400BadRequest, problemDetails);
    }
}
