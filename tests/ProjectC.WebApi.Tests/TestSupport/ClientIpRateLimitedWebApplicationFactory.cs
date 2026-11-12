using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace ProjectC.WebApi.Tests.TestSupport;

// 登入與驗證碼額度覆寫為小額度、長視窗（測試期間不會重置），搭配 ForwardedHeadersWebApplicationFactory 模擬
// 受信任代理／非信任來源。整個測試類別共用一個實例，各測試以互不重複的來源 IP 落入獨立分區
// （reverse-proxy-forwarded-headers design.md 決策 7）。
public sealed class ClientIpRateLimitedWebApplicationFactory : ForwardedHeadersWebApplicationFactory
{
    public const int PermitLimit = 3;
    public const int WindowSeconds = 600;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LoginRateLimiting:PermitLimit"] = PermitLimit.ToString(),
                ["LoginRateLimiting:WindowSeconds"] = WindowSeconds.ToString(),
                ["CaptchaRateLimiting:PermitLimit"] = PermitLimit.ToString(),
                ["CaptchaRateLimiting:WindowSeconds"] = WindowSeconds.ToString(),
            });
        });
    }
}
