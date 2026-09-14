using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Infrastructure.Captcha;

namespace ProjectC.WebApi.Tests.TestSupport;

// CAPTCHA-FAIL-001／002（端到端）：比照既有 RedisUnavailableWebApplicationFactory 手法，指向真實、
// 語法合法但保證無人監聽的 127.0.0.1:1，讓 StackExchange.Redis 走到真正的連線失敗路徑
// （RedisConnectionException），而非人為跳過連線嘗試。MUST 把 ICaptchaService 覆寫回真實的
// RedisCaptchaService——不得沿用 CustomWebApplicationFactory 為其他既有測試新增的 FakeCaptchaService
// 預設值，否則測不到真正的 Redis 連線例外（captcha-verification design.md 決策 5、9）。
public sealed class CaptchaFailClosedWebApplicationFactory : CustomWebApplicationFactory
{
    // 這個 factory 一律把連線字串覆寫成無法連線的 endpoint，基底類別原本會啟動的可正常運作 Redis
    // 容器完全用不到——省下這個容器，減少測試套件平行執行時的 Docker 負載（比照
    // RedisUnavailableWebApplicationFactory 既有慣例）。
    protected override bool NeedsWorkingRedis => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Redis"] = "127.0.0.1:1",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICaptchaService>();
            services.AddScoped<ICaptchaService, RedisCaptchaService>();
        });
    }
}
