using ProjectC.Application.Common;

namespace ProjectC.WebApi.Extensions;

public static class EagerOptionsValidationExtensions
{
    public static WebApplication ResolveEagerlyValidatedOptions(this WebApplication app)
    {
        // RateLimitingOptions 沒有 ValidateOnStart()，這裡強制在啟動時觸發一次 IOptions<T>.Value 解析，
        // 讓 DataAnnotations 驗證確實在應用程式啟動過程中執行，不延遲到第一個進入端點的 HTTP 請求
        // （rate-limiting-queue design.md 決策 1）。MUST 解析 unwrap 後的 RateLimitingOptions（而不只是
        // IOptions<RateLimitingOptions> 這個 wrapper 本身）——只取得 wrapper 不會觸發 .Value 存取，
        // 驗證就不會真的執行；RateLimitingServiceCollectionExtensions 註冊的 Singleton 工廠內部才會呼叫 .Value。
        app.Services.GetRequiredService<RateLimitingOptions>();

        // LoginRateLimitingOptions 同樣沒有 ValidateOnStart()，理由與上面的 RateLimitingOptions 相同
        // （login-rate-limiting design.md 決策 3）。
        app.Services.GetRequiredService<LoginRateLimitingOptions>();

        // CaptchaOptions／CaptchaRateLimitingOptions 同樣沒有 ValidateOnStart()，理由與上面相同
        // （captcha-verification design.md 決策 14、6）。
        app.Services.GetRequiredService<CaptchaOptions>();
        app.Services.GetRequiredService<CaptchaRateLimitingOptions>();

        // SeatMapRateLimitingOptions 同樣沒有 ValidateOnStart()，理由與上面相同（request-body-size-limits design.md 決策 8）。
        app.Services.GetRequiredService<SeatMapRateLimitingOptions>();

        return app;
    }
}
