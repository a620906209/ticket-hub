using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProjectC.Domain.Events;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Repositories;

namespace ProjectC.WebApi.Tests.TestSupport;

// 把 PublicReadRateLimiting 覆寫為小額度、長視窗（測試期間不會重置），並以計數裝飾器觀察 Repository 呼叫次數，
// 驗證 429 時查詢未執行。繼承 ForwardedHeadersWebApplicationFactory 以模擬不同來源 IP。
// 每個測試各自 new 一個實例，不共用限流器狀態（reverse-proxy-forwarded-headers design.md 決策 6、7）。
public sealed class PublicReadRateLimitedWebApplicationFactory : ForwardedHeadersWebApplicationFactory
{
    public const int PermitLimit = 3;
    public const int WindowSeconds = 600;

    public EventRepositoryCallCounter EventRepositoryCallCounter { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PublicReadRateLimiting:PermitLimit"] = PermitLimit.ToString(),
                ["PublicReadRateLimiting:WindowSeconds"] = WindowSeconds.ToString(),
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(EventRepositoryCallCounter);
            services.RemoveAll<IEventRepository>();
            services.AddScoped(sp => new EventRepository(sp.GetRequiredService<ApplicationDbContext>()));
            services.AddScoped<IEventRepository>(sp =>
                new CountingEventRepository(sp.GetRequiredService<EventRepository>(), sp.GetRequiredService<EventRepositoryCallCounter>()));
        });
    }
}
