using Serilog.Events;

namespace ProjectC.WebApi.HealthChecks;

public static class ProbeRequestLogLevel
{
    // 非探測端點比照 Serilog 預設規則（例外或 5xx 為 Error），設定 GetLevel 會整個取代預設，須自行保留。
    public static LogEventLevel GetLevel(HttpContext httpContext, double elapsedMilliseconds, Exception? exception)
    {
        var statusCode = httpContext.Response.StatusCode;

        if (HealthCheckPaths.IsProbePath(httpContext.Request.Path))
        {
            return statusCode == StatusCodes.Status200OK && exception is null
                ? LogEventLevel.Debug
                : LogEventLevel.Error;
        }

        return exception is not null || statusCode >= StatusCodes.Status500InternalServerError
            ? LogEventLevel.Error
            : LogEventLevel.Information;
    }
}
