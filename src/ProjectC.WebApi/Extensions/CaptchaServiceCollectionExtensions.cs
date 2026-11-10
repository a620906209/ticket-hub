using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ProjectC.Application.Captcha.GetCaptcha;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Infrastructure.Captcha;

namespace ProjectC.WebApi.Extensions;

public static class CaptchaServiceCollectionExtensions
{
    public static IServiceCollection AddCaptcha(this IServiceCollection services, IConfiguration configuration)
    {
        // CaptchaOptions：有安全的預設值（TtlSeconds = 120），比照 CaptchaRateLimitingOptions／
        // LoginRateLimitingOptions 不需要 ValidateOnStart，缺漏時採用預設值、顯式設為 0 或負數才擋下
        // （captcha-verification design.md 決策 14）。
        services
            .AddOptions<CaptchaOptions>()
            .Bind(configuration.GetSection(CaptchaOptions.SectionName))
            .ValidateDataAnnotations();
        services.AddSingleton<IValidateOptions<CaptchaOptions>, CaptchaOptionsValidator>();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<CaptchaOptions>>().Value);

        // Scoped，MUST NOT 是 Singleton：SixLabors.Fonts 的 FontFamily／FontCollection 未經查證取得
        // 多執行緒並行讀取的官方 thread-safety 保證，Scoped 讓每個 request 各自持有獨立執行個體，
        // 從根本排除跨執行緒共用同一份字型物件的疑慮（captcha-verification design.md 決策 13）。
        services.AddScoped<ICaptchaImageGenerator, CaptchaImageGenerator>();
        services.AddCaptchaService();
        services.AddScoped<GetCaptchaHandler>();

        return services;
    }

    // 獨立成公開方法，讓測試工廠移除替身後能以同一個註冊重新註冊，確保測到的是模式選擇而不是替身。
    public static IServiceCollection AddCaptchaService(this IServiceCollection services)
    {
        services.TryAddScoped<RedisCaptchaService>();
        services.AddScoped<ICaptchaService>(serviceProvider =>
        {
            var redisCaptchaService = serviceProvider.GetRequiredService<RedisCaptchaService>();
            var options = serviceProvider.GetRequiredService<CaptchaOptions>();

            return options.VerificationMode == CaptchaVerificationMode.AcceptAny
                ? new AcceptAnyCaptchaService(redisCaptchaService)
                : redisCaptchaService;
        });

        return services;
    }
}
