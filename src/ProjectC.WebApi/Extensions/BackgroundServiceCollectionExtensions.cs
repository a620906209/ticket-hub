using ProjectC.WebApi.BackgroundServices;

namespace ProjectC.WebApi.Extensions;

public static class BackgroundServiceCollectionExtensions
{
    public static IServiceCollection AddBackgroundServices(this IServiceCollection services, IHostEnvironment environment)
    {
        // Testing 環境（見 CustomWebApplicationFactory.UseEnvironment("Testing")）不啟動真實背景服務，
        // 否則所有既有 WebApi 整合測試都會連帶啟動一個對著自己 Testcontainers 資料庫跑的清理服務
        // （見 ticketing-order-management design.md 決策 5）。
        if (!environment.IsEnvironment("Testing"))
        {
            services.AddHostedService<ExpiredOrderCleanupService>();
            services.AddHostedService<PurchaseQueueAdmissionService>();
        }

        return services;
    }
}
