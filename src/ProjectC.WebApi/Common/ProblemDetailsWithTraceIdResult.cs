using Microsoft.AspNetCore.Mvc;

namespace ProjectC.WebApi.Common;

// ResultExtensions 是無 HttpContext 的靜態方法，traceId 只能在結果執行時取得
// （error-response-title-traceid design.md 決策 2）。
public sealed class ProblemDetailsWithTraceIdResult : IActionResult
{
    private readonly ProblemDetails _problemDetails;

    public ProblemDetailsWithTraceIdResult(ProblemDetails problemDetails)
    {
        _problemDetails = problemDetails;
    }

    public Task ExecuteResultAsync(ActionContext context)
    {
        _problemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;

        var objectResult = new ObjectResult(_problemDetails) { StatusCode = _problemDetails.Status };
        return objectResult.ExecuteResultAsync(context);
    }
}
