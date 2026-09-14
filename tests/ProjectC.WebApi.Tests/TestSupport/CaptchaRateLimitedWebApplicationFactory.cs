using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace ProjectC.WebApi.Tests.TestSupport;

// 覆寫 CaptchaRateLimitingOptions 為小額度、短視窗，讓 captcha policy 相關測試能快速觸發拒絕，
// 不需要真的送出上百次請求（比照既有 RateLimitedWebApplicationFactory 手法）。繼承
// CaptchaComponentTestWebApplicationFactory，讓 GET /api/captcha 走真實 RedisCaptchaService
// （CAPTCHA-RATE-001 需要真實服務才能驗證，見 design.md 決策 9）。不與其他測試共用同一組額度。
public class CaptchaRateLimitedWebApplicationFactory : CaptchaComponentTestWebApplicationFactory
{
    public const int PermitLimit = 3;
    public const int WindowSeconds = 2;

    // 同時覆寫 LoginRateLimiting 為小額度，讓 CAPTCHA-RATE-004 能驗證「login policy 已達上限時，
    // captcha policy 依自身額度正常處理」這個反向情境，不需要真的送出千次請求打滿
    // CustomWebApplicationFactory 預設放寬的 1000 額度。
    public const int LoginPermitLimit = 3;
    public const int LoginWindowSeconds = 2;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CaptchaRateLimiting:PermitLimit"] = PermitLimit.ToString(),
                ["CaptchaRateLimiting:WindowSeconds"] = WindowSeconds.ToString(),
                ["LoginRateLimiting:PermitLimit"] = LoginPermitLimit.ToString(),
                ["LoginRateLimiting:WindowSeconds"] = LoginWindowSeconds.ToString(),
            });
        });
    }
}
