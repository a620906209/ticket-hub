using Microsoft.Extensions.Options;
using ProjectC.Application.Common;
using ProjectC.Domain.Notifications;
using ProjectC.Domain.Payments;
using ProjectC.Domain.Tickets;
using ProjectC.Infrastructure.Notifications;
using ProjectC.Infrastructure.Payments;
using ProjectC.Infrastructure.Tickets;

namespace ProjectC.WebApi.Extensions;

public static class TicketingServiceCollectionExtensions
{
    public static IServiceCollection AddTicketingServices(this IServiceCollection services, IConfiguration configuration)
    {
        // OrderCleanupOptions 有安全的預設值，不像 JwtOptions 缺值就無法運作，不需要 ValidateOnStart（見 design.md 決策 2）。
        services.Configure<OrderCleanupOptions>(configuration.GetSection("OrderCleanup"));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<OrderCleanupOptions>>().Value);

        // MockPaymentGatewayOptions 有安全的預設值（AlwaysSucceed = true），比照 OrderCleanupOptions 不需要 ValidateOnStart
        // （見 order-payment-gateway-alignment design.md 決策 2）。
        services.Configure<MockPaymentGatewayOptions>(configuration.GetSection(MockPaymentGatewayOptions.SectionName));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<MockPaymentGatewayOptions>>().Value);
        services.AddSingleton<IPaymentGateway, MockPaymentGateway>();

        // MockEmailNotificationServiceOptions 有安全的預設值（AlwaysSucceed = true），比照 MockPaymentGatewayOptions
        // 不需要 ValidateOnStart（見 email-notification design.md 決策 4）。
        services.Configure<MockEmailNotificationServiceOptions>(configuration.GetSection(MockEmailNotificationServiceOptions.SectionName));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<MockEmailNotificationServiceOptions>>().Value);
        services.AddSingleton<IEmailNotificationService, MockEmailNotificationService>();

        // Production 綁定上面兩個 Mock 時預設拒絕啟動，須以 Mocks:AllowInProduction=true 明示放行；
        // 缺漏時視為 false，故不需要 ValidateOnStart，改由 ResolveEagerlyValidatedOptions 強制解析
        // （mock-service-production-guard design.md 決策 2）。
        services
            .AddOptions<MockServicesOptions>()
            .Bind(configuration.GetSection(MockServicesOptions.SectionName));
        services.AddSingleton<IValidateOptions<MockServicesOptions>, MockServicesOptionsValidator>();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<MockServicesOptions>>().Value);

        // TicketSigningOptions：比照 JwtOptions 啟動時 fail-fast 驗證，簽章金鑰缺失或過弱直接讓應用程式啟動失敗
        // （見 design.md 決策 3）；再比照 AuthOptions/OrderCleanupOptions/MockPaymentGatewayOptions 解包成
        // 一般 class 註冊為 Singleton，讓 HmacTicketSigningService 能直接建構子注入。
        services
            .AddOptions<TicketSigningOptions>()
            .Bind(configuration.GetSection(TicketSigningOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<TicketSigningOptions>>().Value);

        // HmacTicketSigningService 無狀態，比照 MockPaymentGateway 的 Singleton 選擇
        // （純運算、thread-safe，不持有任何 DbContext 或 request-scoped 狀態）。
        services.AddSingleton<ITicketSigningService, HmacTicketSigningService>();
        services.AddTransient<ITicketQrCodeGenerator, TicketQrCodeGenerator>();

        // PurchaseQueueOptions：任一值缺漏或為 0／負數都會讓熱門搶購模式的活動功能完全失效，
        // 比照 JwtOptions/TicketSigningOptions 啟動時 fail-fast（見 design.md 決策 3）。
        services
            .AddOptions<PurchaseQueueOptions>()
            .Bind(configuration.GetSection(PurchaseQueueOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<PurchaseQueueOptions>>().Value);

        return services;
    }
}
