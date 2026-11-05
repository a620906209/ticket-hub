using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using ProjectC.Application.Common;
using ProjectC.WebApi.Common;

namespace ProjectC.WebApi.Extensions;

public static class RateLimitingServiceCollectionExtensions
{
    public static IServiceCollection AddRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        // RateLimitingOptions 有安全的預設值，不像 PurchaseQueueOptions 缺值就無法運作，不需要 ValidateOnStart；
        // 只能用 AddOptions<T>().Bind(...).ValidateDataAnnotations()——Configure<T>() 回傳 IServiceCollection，
        // 無法直接串接 ValidateDataAnnotations()（見 rate-limiting-queue design.md 決策 1）。
        services
            .AddOptions<RateLimitingOptions>()
            .Bind(configuration.GetSection(RateLimitingOptions.SectionName))
            .ValidateDataAnnotations();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<RateLimitingOptions>>().Value);

        // LoginRateLimitingOptions：與 RateLimitingOptions 分開的獨立設定類別，分區鍵語意不同（來源 IP vs
        // 已登入會員 Id），數值也刻意設得更嚴格（login-rate-limiting design.md 決策 2）；同樣有安全預設值，
        // 不需要 ValidateOnStart，寫法逐行比照上面的 RateLimitingOptions。
        services
            .AddOptions<LoginRateLimitingOptions>()
            .Bind(configuration.GetSection(LoginRateLimitingOptions.SectionName))
            .ValidateDataAnnotations();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<LoginRateLimitingOptions>>().Value);

        // CaptchaRateLimitingOptions：獨立於 RateLimitingOptions／LoginRateLimitingOptions，比照同一種
        // 有安全預設值、不需要 ValidateOnStart 的模式（captcha-verification design.md 決策 6）。
        services
            .AddOptions<CaptchaRateLimitingOptions>()
            .Bind(configuration.GetSection(CaptchaRateLimitingOptions.SectionName))
            .ValidateDataAnnotations();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<CaptchaRateLimitingOptions>>().Value);

        // SeatMapRateLimitingOptions：比照 CaptchaRateLimitingOptions 同一種有安全預設值、不需要 ValidateOnStart
        // 的模式（request-body-size-limits design.md 決策 8）。
        services
            .AddOptions<SeatMapRateLimitingOptions>()
            .Bind(configuration.GetSection(SeatMapRateLimitingOptions.SectionName))
            .ValidateDataAnnotations();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<SeatMapRateLimitingOptions>>().Value);

        // place-order/confirm-order 是分區鍵為會員 Id 的獨立命名 Fixed Window policy，各自累計、不共用計數
        // （rate-limiting-queue design.md 決策 1）；login 呼叫當下使用者尚未通過驗證，改以來源 IP 分區
        // （login-rate-limiting design.md 決策 1）。三個 policy 各自獨立計數，皆共用下方同一個 OnRejected。
        services.AddRateLimiter(rateLimiterOptions =>
        {
            rateLimiterOptions.AddPolicy("place-order", httpContext => CreateMemberPartition(httpContext));
            rateLimiterOptions.AddPolicy("confirm-order", httpContext => CreateMemberPartition(httpContext));
            rateLimiterOptions.AddPolicy("login", httpContext => CreateIpPartition(httpContext));
            // 獨立命名的 captcha policy，MUST NOT 沿用 "login"——共用 policy 名稱會共用同一個
            // PartitionedRateLimiter 計數器（captcha-verification design.md 決策 6）。
            rateLimiterOptions.AddPolicy("captcha", httpContext => CreateCaptchaPartition(httpContext));
            // 建立座位圖放寬到 10MB 本文，以 Organizer 分區限制單一 Organizer 的頻率
            // （request-body-size-limits design.md 決策 8）。
            rateLimiterOptions.AddPolicy("create-seat-map", httpContext => CreateOrganizerPartition(httpContext));

            rateLimiterOptions.OnRejected = async (context, cancellationToken) =>
            {
                int? retryAfterSeconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                    ? (int)retryAfter.TotalSeconds
                    : null;

                if (retryAfterSeconds is { } seconds)
                {
                    context.HttpContext.Response.Headers["Retry-After"] = seconds.ToString();
                }

                var problemDetails = new ProblemDetails
                {
                    Status = StatusCodes.Status429TooManyRequests,
                    Title = "TooManyRequests",
                    Extensions = { ["traceId"] = context.HttpContext.TraceIdentifier },
                };

                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.HttpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken);
            };

            static RateLimitPartition<string> CreateMemberPartition(HttpContext httpContext)
            {
                var options = httpContext.RequestServices.GetRequiredService<RateLimitingOptions>();
                var partitionKey = httpContext.User.GetMemberId().ToString();

                return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.PermitLimit,
                    Window = TimeSpan.FromSeconds(options.WindowSeconds),
                    QueueLimit = 0,
                });
            }

            // 登入端點呼叫當下使用者尚未通過驗證，沒有會員 Id 可用，改以來源 IP 分區
            // （login-rate-limiting design.md 決策 1、3）。
            static RateLimitPartition<string> CreateIpPartition(HttpContext httpContext)
            {
                var options = httpContext.RequestServices.GetRequiredService<LoginRateLimitingOptions>();
                var partitionKey = LoginRateLimiterPartitioning.GetPartitionKey(httpContext);

                return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.PermitLimit,
                    Window = TimeSpan.FromSeconds(options.WindowSeconds),
                    QueueLimit = 0,
                });
            }

            // GET /api/captcha 同樣匿名可呼叫、沒有會員 Id，重用既有的 IP 分區鍵推導邏輯，但改讀取
            // 獨立的 CaptchaRateLimitingOptions——這只是共用「怎麼從 HttpContext 算出 IP 字串」的
            // 靜態方法，不代表共用 login policy 本身或其計數狀態（captcha-verification design.md 決策 6）。
            static RateLimitPartition<string> CreateCaptchaPartition(HttpContext httpContext)
            {
                var options = httpContext.RequestServices.GetRequiredService<CaptchaRateLimitingOptions>();
                var partitionKey = LoginRateLimiterPartitioning.GetPartitionKey(httpContext);

                return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.PermitLimit,
                    Window = TimeSpan.FromSeconds(options.WindowSeconds),
                    QueueLimit = 0,
                });
            }

            // 同一 Organizer 的多個成員共用額度，擋多帳號聚合。授權早於 UseRateLimiter，正常情況 claim
            // 一定合法；若仍取不到則共用固定鍵，fail-closed，不會因取不到分區鍵而不限流。
            static RateLimitPartition<string> CreateOrganizerPartition(HttpContext httpContext)
            {
                var options = httpContext.RequestServices.GetRequiredService<SeatMapRateLimitingOptions>();
                var partitionKey = httpContext.User.TryGetOrganizerId(out var organizerId)
                    ? organizerId.ToString()
                    : "no-organizer";

                return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.PermitLimit,
                    Window = TimeSpan.FromSeconds(options.WindowSeconds),
                    QueueLimit = 0,
                });
            }
        });

        return services;
    }
}
