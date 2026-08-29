using Microsoft.AspNetCore.Mvc;
using ProjectC.Application.Common;

namespace ProjectC.WebApi.Common;

public static class ResultExtensions
{
    public static IActionResult ToActionResult(this Result result)
    {
        return result.IsSuccess ? new NoContentResult() : CreateProblemResult(result.Error!);
    }

    public static IActionResult ToActionResult<T>(this Result<T> result, Func<T, IActionResult> onSuccess)
    {
        return result.IsSuccess ? onSuccess(result.Value!) : CreateProblemResult(result.Error!);
    }

    private static IActionResult CreateProblemResult(Error error)
    {
        var statusCode = error.Type switch
        {
            ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorType.Forbidden => StatusCodes.Status403Forbidden,
            // Title 沿用下方 error.Type.ToString()，天然就是穩定的 "QueueAdmissionRequired" 字串，
            // 前端據此（而非泛用 403）判斷是否導向排隊等待畫面（rate-limiting-queue design.md 決策 4）。
            ErrorType.QueueAdmissionRequired => StatusCodes.Status403Forbidden,
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        };

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = error.Type.ToString(),
            Detail = error.Message,
        };

        return new ObjectResult(problemDetails) { StatusCode = statusCode };
    }
}
