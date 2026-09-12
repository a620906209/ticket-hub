using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProjectC.Domain.Events;
using ProjectC.Domain.Tickets;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Repositories;

namespace ProjectC.WebApi.Tests.TestSupport;

// query-caching tasks.md 2.6／3.7／8.2a／8.2b 共用：把 IEventRepository／ITicketTypeRepository
// 換成計數裝飾器，底層行為完全委派給真正的 EventRepository／TicketTypeRepository（真的查資料庫），
// 用來驗證「快取命中時 Repository 未被重複查詢」。計數器本身註冊為 Singleton，跨多次 HTTP 請求
// （各自獨立的 DI scope）累計同一份計數。
public sealed class CachingComponentTestWebApplicationFactory : CustomWebApplicationFactory
{
    public EventRepositoryCallCounter EventRepositoryCallCounter { get; } = new();

    public TicketTypeRepositoryCallCounter TicketTypeRepositoryCallCounter { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(EventRepositoryCallCounter);
            services.RemoveAll<IEventRepository>();
            services.AddScoped(sp => new EventRepository(sp.GetRequiredService<ApplicationDbContext>()));
            services.AddScoped<IEventRepository>(sp =>
                new CountingEventRepository(sp.GetRequiredService<EventRepository>(), sp.GetRequiredService<EventRepositoryCallCounter>()));

            services.AddSingleton(TicketTypeRepositoryCallCounter);
            services.RemoveAll<ITicketTypeRepository>();
            services.AddScoped(sp => new TicketTypeRepository(sp.GetRequiredService<ApplicationDbContext>()));
            services.AddScoped<ITicketTypeRepository>(sp =>
                new CountingTicketTypeRepository(sp.GetRequiredService<TicketTypeRepository>(), sp.GetRequiredService<TicketTypeRepositoryCallCounter>()));
        });
    }
}
