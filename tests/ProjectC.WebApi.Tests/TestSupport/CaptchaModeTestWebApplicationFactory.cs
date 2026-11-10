using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProjectC.Application.Common.Interfaces;
using ProjectC.WebApi.Extensions;

namespace ProjectC.WebApi.Tests.TestSupport;

// 驗證碼模式相關的整合測試專用：CustomWebApplicationFactory 預設以 FakeCaptchaService 覆寫、
// CaptchaComponentTestWebApplicationFactory 強制註冊 RedisCaptchaService，兩者都繞過依模式選實作的工廠。
// 這裡移除替身後以 AddCaptchaService 重新註冊，保留 AddCaptcha 的模式選擇（captcha-test-mode tasks.md 1.0）。
// 共用同一組 Testcontainers 的 DB／Redis，以 WithMode 取得不同模式的衍生工廠。
public class CaptchaModeTestWebApplicationFactory : CustomWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICaptchaService>();
            services.AddCaptchaService();
        });
    }

    // mode 為 null 表示不設定該鍵（缺漏情境）。
    public WebApplicationFactory<Program> WithMode(string? mode)
        => WithWebHostBuilder(builder =>
        {
            if (mode is not null)
            {
                builder.UseSetting("Captcha:VerificationMode", mode);
            }
        });

    // 準備測試前置資料（註冊、登入、建活動）用：以固定答案的替身通過核對，與被測模式無關。
    public WebApplicationFactory<Program> WithFakeCaptcha()
        => WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICaptchaService>();
            services.AddScoped<ICaptchaService, FakeCaptchaService>();
        }));
}
