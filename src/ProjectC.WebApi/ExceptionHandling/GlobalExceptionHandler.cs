using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace ProjectC.WebApi.ExceptionHandling;

public class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var problemDetails = exception is BadHttpRequestException badHttpRequestException
            ? CreateBadRequestProblemDetails(httpContext, badHttpRequestException.StatusCode)
            : CreateUnexpectedErrorProblemDetails(httpContext, exception);

        httpContext.Response.StatusCode = problemDetails.Status!.Value;

        await httpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken);

        return true;
    }

    // BadHttpRequestException 的 StatusCode 由框架判定為請求錯誤（本文過大 413、格式錯誤 400、逾時 408），
    // 回 500 不正確；日誌刻意不傳入例外物件、不帶 Message，因為 MVC 版的訊息可能含請求細節
    // （request-body-size-limits design.md 決策 4）。
    private ProblemDetails CreateBadRequestProblemDetails(HttpContext httpContext, int statusCode)
    {
        // UseExceptionHandler 呼叫 handler 前會清除 HttpContext 的 endpoint，原本比對到的 endpoint 只保留在 IExceptionHandlerFeature。
        var endpoint = httpContext.Features.Get<IExceptionHandlerFeature>()?.Endpoint ?? httpContext.GetEndpoint();
        var routeTemplate = (endpoint as RouteEndpoint)?.RoutePattern.RawText ?? "(unmatched)";

        _logger.LogWarning(
            "Bad HTTP request {StatusCode} {Method} {RouteTemplate}. TraceId: {TraceId}",
            statusCode,
            httpContext.Request.Method,
            routeTemplate,
            httpContext.TraceIdentifier);

        return new ProblemDetails
        {
            Status = statusCode,
            Title = statusCode == StatusCodes.Status413PayloadTooLarge
                ? "Request body too large."
                : ReasonPhrases.GetReasonPhrase(statusCode),
            Extensions = { ["traceId"] = httpContext.TraceIdentifier },
        };
    }

    private ProblemDetails CreateUnexpectedErrorProblemDetails(HttpContext httpContext, Exception exception)
    {
        _logger.LogError(
            exception,
            "Unhandled exception. TraceId: {TraceId}",
            httpContext.TraceIdentifier);

        return new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "An unexpected error occurred.",
            Extensions = { ["traceId"] = httpContext.TraceIdentifier },
        };
    }
}
