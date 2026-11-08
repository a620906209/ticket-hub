using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using ProjectC.Application.Common;
using ProjectC.WebApi.Common;

namespace ProjectC.WebApi.Extensions;

public static class ModelStateResponseServiceCollectionExtensions
{
    private const string DefaultMessage = "The input was not valid.";

    // 必須在 AddControllers() 之後呼叫，否則 MVC 的預設設定會覆蓋這裡的 InvalidModelStateResponseFactory。
    public static IServiceCollection AddValidationProblemResponses(this IServiceCollection services)
    {
        services.Configure<ApiBehaviorOptions>(options => options.InvalidModelStateResponseFactory = CreateInvalidModelStateResponse);
        return services;
    }

    // 與 Handler 路徑共用 ProblemDetailsWithTraceIdResult：traceId 取 HttpContext.TraceIdentifier，
    // 不經 ProblemDetailsFactory（其 traceId 為 Activity id，與其他錯誤回應不同）
    // （validation-error-fields design.md 決策 5）。
    public static IActionResult CreateInvalidModelStateResponse(ActionContext context)
    {
        var bodyParameterNames = context.ActionDescriptor.Parameters
            .Where(parameter => parameter.BindingInfo?.BindingSource == BindingSource.Body)
            .Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.Ordinal);

        var fieldErrors = new List<KeyValuePair<string, string[]>>();
        var parameterNameMessages = new List<string>();
        foreach (var (key, entry) in context.ModelState)
        {
            var messages = entry.Errors
                .Select(error => string.IsNullOrEmpty(error.ErrorMessage) ? DefaultMessage : error.ErrorMessage)
                .ToArray();
            if (messages.Length == 0)
            {
                continue;
            }

            if (bodyParameterNames.Contains(key))
            {
                parameterNameMessages.AddRange(messages);
                continue;
            }

            fieldErrors.Add(new KeyValuePair<string, string[]>(key, messages));
        }

        // 濾除參數名鍵後若無任何鍵，訊息改列於請求本文層級的空字串鍵，確保 errors 鍵數大於 0。
        if (fieldErrors.Count == 0 && parameterNameMessages.Count > 0)
        {
            fieldErrors.Add(new KeyValuePair<string, string[]>(string.Empty, [.. parameterNameMessages]));
        }

        var problemDetails = new ValidationProblemDetails(FieldErrorKeyFormatter.FormatAll(fieldErrors))
        {
            Status = StatusCodes.Status400BadRequest,
            Title = ErrorType.Validation.ToString(),
        };
        return new ProblemDetailsWithTraceIdResult(problemDetails);
    }
}
