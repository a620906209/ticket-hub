using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Infrastructure.Captcha;

namespace ProjectC.WebApi.Tests.TestSupport;

// 需要驗證真實 RedisCaptchaService 行為的測試（CAPTCHA-GEN-*／CAPTCHA-RATE-001，見 tasks.md
// 6.1／6.2）把 CustomWebApplicationFactory 預設替換回去的 FakeCaptchaService 換回真實服務
// ——比照 NeedsWorkingRedis 這種既有透過子類別覆寫改變基底行為的既定慣例（captcha-verification
// design.md 決策 9）。ICaptchaImageGenerator／CaptchaOptions 沿用 Program.cs 既有註冊，不需要覆寫。
public class CaptchaComponentTestWebApplicationFactory : CustomWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICaptchaService>();
            services.AddScoped<ICaptchaService, RedisCaptchaService>();
        });
    }
}
