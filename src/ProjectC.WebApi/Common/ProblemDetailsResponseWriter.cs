using Microsoft.AspNetCore.Mvc;

namespace ProjectC.WebApi.Common;

// 框架層錯誤回應共用的寫出步驟，Content-Type 只在此定義（problem-json-content-type design.md 決策 1）；
// 狀態碼與 header 由呼叫端在呼叫前設定。
public static class ProblemDetailsResponseWriter
{
    private const string ProblemJsonContentType = "application/problem+json";

    public static Task WriteAsync(HttpContext httpContext, ProblemDetails problemDetails, CancellationToken cancellationToken)
    {
        return httpContext.Response.WriteAsJsonAsync(
            problemDetails, options: null, contentType: ProblemJsonContentType, cancellationToken);
    }
}
