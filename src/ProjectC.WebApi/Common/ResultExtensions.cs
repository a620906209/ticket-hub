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
            // Title 沿用下方 error.Type.ToString()，前端據此（而非泛用 400）判斷是否顯示
            // 「簽章驗證失敗」，比照 QueueAdmissionRequired 的既有慣例（redemption-scanner-ui design.md 決策 2）。
            ErrorType.InvalidTicketSignature => StatusCodes.Status400BadRequest,
            // Title 沿用下方 error.Type.ToString()，前端據此（而非泛用 400 Validation）判斷是否為驗證碼
            // 答案錯誤，比照 QueueAdmissionRequired／InvalidTicketSignature 的既有慣例——若只依 HTTP
            // status 判斷，其他語意的 400（例如 Email／密碼欄位驗證失敗）會被誤判成驗證碼錯誤，導致
            // 前端誤清空驗證碼輸入並不必要地換發新圖（captcha-verification design.md 決策 8 補充）。
            ErrorType.CaptchaInvalid => StatusCodes.Status400BadRequest,
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
