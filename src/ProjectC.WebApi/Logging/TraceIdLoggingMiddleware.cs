using Serilog.Context;

namespace ProjectC.WebApi.Logging;

// 把每次請求的 HttpContext.TraceIdentifier 推進 Serilog LogContext，讓該次請求範圍內所有日誌
// （含 UseSerilogRequestLogging 產生的摘要日誌、Handler／Repository 拋出的例外）自動帶上同一個
// TraceId 欄位，與既有 GlobalExceptionHandler 回傳給前端的 traceId 是同一個值
// （observability design.md 決策 2）。
public sealed class TraceIdLoggingMiddleware
{
    private readonly RequestDelegate _next;

    public TraceIdLoggingMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext httpContext)
    {
        using (LogContext.PushProperty("TraceId", httpContext.TraceIdentifier))
        {
            await _next(httpContext);
        }
    }
}
