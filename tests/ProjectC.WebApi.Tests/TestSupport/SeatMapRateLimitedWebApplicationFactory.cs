using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace ProjectC.WebApi.Tests.TestSupport;

// 覆寫 SeatMapRateLimitingOptions 為小額度，讓 create-seat-map policy 測試能快速觸發 429；視窗刻意放長，
// 避免測試途中視窗重置讓第 3 次請求意外通過（比照 CaptchaRateLimitedWebApplicationFactory）。
// 不與其他測試共用同一組額度。
public sealed class SeatMapRateLimitedWebApplicationFactory : CustomWebApplicationFactory
{
    public const int PermitLimit = 2;
    public const int WindowSeconds = 300;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SeatMapRateLimiting:PermitLimit"] = PermitLimit.ToString(),
                ["SeatMapRateLimiting:WindowSeconds"] = WindowSeconds.ToString(),
            });
        });
    }
}
