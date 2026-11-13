using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace ProjectC.WebApi.ExceptionHandling;

public class GlobalExceptionHandler : IExceptionHandler
{
    private const string ProblemJsonContentType = "application/problem+json";

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
        var problemDetails = exception switch
        {
            BadHttpRequestException badHttpRequestException
                => CreateBadRequestProblemDetails(httpContext, badHttpRequestException.StatusCode),
            _ when DatabaseTimeoutDetector.IsDatabaseTimeout(exception)
                => CreateDatabaseTimeoutProblemDetails(httpContext, exception),
            _ => CreateUnexpectedErrorProblemDetails(httpContext, exception),
        };

        httpContext.Response.StatusCode = problemDetails.Status!.Value;

        // 資料庫逾時依 api-error-responses 規格為 application/problem+json；其餘分支維持既有的預設 content type。
        var contentType = problemDetails.Status == StatusCodes.Status503ServiceUnavailable ? ProblemJsonContentType : null;
        await httpContext.Response.WriteAsJsonAsync(problemDetails, options: null, contentType, cancellationToken);

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
            Title = CreateBadRequestTitle(statusCode),
            Extensions = { ["traceId"] = httpContext.TraceIdentifier },
        };
    }

    // 常見狀態碼用明確對照，不依賴 reason phrase 在框架版本間是否改動；其餘退回 reason phrase 僅保留英數字元。
    private static string CreateBadRequestTitle(int statusCode)
    {
        switch (statusCode)
        {
            case StatusCodes.Status413PayloadTooLarge:
                return "PayloadTooLarge";
            case StatusCodes.Status408RequestTimeout:
                return "RequestTimeout";
            case StatusCodes.Status400BadRequest:
                return "BadRequest";
        }

        var code = new string(ReasonPhrases.GetReasonPhrase(statusCode).Where(char.IsAsciiLetterOrDigit).ToArray());
        return code.Length == 0 ? "BadRequest" : code;
    }

    // 資料庫暫時無法即時服務而非程式缺陷，回 503；日誌刻意不傳入例外物件、只記型別名稱，因為 Npgsql 例外的訊息與 Data
    // 可能夾帶 SQL 或連線資訊（db-timeout-pool-tuning design.md 決策 5）。
    private ProblemDetails CreateDatabaseTimeoutProblemDetails(HttpContext httpContext, Exception exception)
    {
        var endpoint = httpContext.Features.Get<IExceptionHandlerFeature>()?.Endpoint ?? httpContext.GetEndpoint();
        var routeTemplate = (endpoint as RouteEndpoint)?.RoutePattern.RawText ?? "(unmatched)";

        _logger.LogWarning(
            "Database timeout {ExceptionType} {Method} {RouteTemplate}. TraceId: {TraceId}",
            exception.GetType().Name,
            httpContext.Request.Method,
            routeTemplate,
            httpContext.TraceIdentifier);

        return new ProblemDetails
        {
            Status = StatusCodes.Status503ServiceUnavailable,
            Title = "ServiceUnavailable",
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
            Title = "InternalServerError",
            Extensions = { ["traceId"] = httpContext.TraceIdentifier },
        };
    }
}
