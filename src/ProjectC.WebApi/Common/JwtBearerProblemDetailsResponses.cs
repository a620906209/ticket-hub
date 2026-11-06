using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;

namespace ProjectC.WebApi.Common;

// JwtBearer 預設的 401／403 不寫 body；改為與其他錯誤一致的 ProblemDetails（不含 detail，
// 失敗原因已在 WWW-Authenticate，見 jwt-challenge-problem-details design.md 決策 1～3）。
public static class JwtBearerProblemDetailsResponses
{
    public static Task WriteChallengeAsync(JwtBearerChallengeContext context)
    {
        // 預設流程在事件之後才設狀態碼與 header，先寫 body 會讓 header 設不上，因此接手並重建與預設相同的 header。
        context.HandleResponse();

        var response = context.Response;
        if (response.HasStarted)
        {
            return Task.CompletedTask;
        }

        response.StatusCode = StatusCodes.Status401Unauthorized;
        response.Headers.Append("WWW-Authenticate", BuildChallengeHeader(context));
        return WriteProblemAsync(context.HttpContext, StatusCodes.Status401Unauthorized, "Unauthorized");
    }

    public static Task WriteForbiddenAsync(ForbiddenContext context)
    {
        if (context.Response.HasStarted)
        {
            return Task.CompletedTask;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return WriteProblemAsync(context.HttpContext, StatusCodes.Status403Forbidden, "Forbidden");
    }

    // 格式比照 JwtBearerHandler.HandleChallengeAsync；是否逐字相同由整合測試以固定基準守護。
    private static string BuildChallengeHeader(JwtBearerChallengeContext context)
    {
        var scheme = context.Options.Challenge;
        if (string.IsNullOrEmpty(context.Error)
            && string.IsNullOrEmpty(context.ErrorDescription)
            && string.IsNullOrEmpty(context.ErrorUri))
        {
            return scheme;
        }

        var builder = new StringBuilder(scheme);
        if (scheme.IndexOf(' ') > 0)
        {
            builder.Append(',');
        }

        if (!string.IsNullOrEmpty(context.Error))
        {
            builder.Append(" error=\"").Append(context.Error).Append('"');
        }

        if (!string.IsNullOrEmpty(context.ErrorDescription))
        {
            if (!string.IsNullOrEmpty(context.Error))
            {
                builder.Append(',');
            }

            builder.Append(" error_description=\"").Append(context.ErrorDescription).Append('"');
        }

        if (!string.IsNullOrEmpty(context.ErrorUri))
        {
            if (!string.IsNullOrEmpty(context.Error) || !string.IsNullOrEmpty(context.ErrorDescription))
            {
                builder.Append(',');
            }

            builder.Append(" error_uri=\"").Append(context.ErrorUri).Append('"');
        }

        return builder.ToString();
    }

    private static Task WriteProblemAsync(HttpContext httpContext, int status, string title)
    {
        var problemDetails = new ProblemDetails
        {
            Status = status,
            Title = title,
            Extensions = { ["traceId"] = httpContext.TraceIdentifier },
        };

        return httpContext.Response.WriteAsJsonAsync(
            problemDetails, options: null, contentType: "application/problem+json", httpContext.RequestAborted);
    }
}
