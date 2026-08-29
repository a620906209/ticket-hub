using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace ProjectC.WebApi.Tests.TestSupport;

/// <summary>覆寫 RateLimitingOptions 為小額度、短視窗，讓限流相關測試能快速觸發拒絕與視窗重置，
/// 不需要真的送出上百次請求或等一分鐘（見 api-rate-limiting spec RL-001~007）。</summary>
public class RateLimitedWebApplicationFactory : CustomWebApplicationFactory
{
    public const int PermitLimit = 3;
    public const int WindowSeconds = 2;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimiting:PermitLimit"] = PermitLimit.ToString(),
                ["RateLimiting:WindowSeconds"] = WindowSeconds.ToString(),
            });
        });
    }
}
