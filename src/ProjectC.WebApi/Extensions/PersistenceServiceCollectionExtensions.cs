using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Events;
using ProjectC.Domain.Members;
using ProjectC.Domain.Orders;
using ProjectC.Domain.PurchaseQueue;
using ProjectC.Domain.Tickets;
using ProjectC.Domain.Venues;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Repositories;

namespace ProjectC.WebApi.Extensions;

public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateDataAnnotations();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<DatabaseOptions>>().Value);

        services.AddApplicationDbContext();
        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<ApplicationDbContext>());

        // Scoped：都綁定同一個 DbContext 的生命週期（一次 HTTP request／一個測試範圍一個 instance）。
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IVenueRepository, VenueRepository>();
        services.AddScoped<ISeatMapRepository, SeatMapRepository>();
        services.AddScoped<IEventRepository, EventRepository>();
        services.AddScoped<IEventSeatRepository, EventSeatRepository>();
        services.AddScoped<ITicketTypeRepository, TicketTypeRepository>();
        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<ITicketRepository, TicketRepository>();
        services.AddScoped<IPurchaseQueueRepository, PurchaseQueueRepository>();
        services.AddScoped<IMemberDisplayNameReader, MemberDisplayNameReader>();
        services.AddScoped<IMemberRealNameRepository, MemberRealNameRepository>();

        return services;
    }

    // 抽成獨立方法讓測試專用 factory 能在移除測試 helper 的 DbContext 註冊後，重新呼叫同一份正式註冊碼
    // （db-timeout-pool-tuning design.md 決策 2）。
    public static IServiceCollection AddApplicationDbContext(this IServiceCollection services)
    {
        services.AddDbContext<ApplicationDbContext>((serviceProvider, options) =>
        {
            var connectionString = serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection");
            var databaseOptions = serviceProvider.GetRequiredService<DatabaseOptions>();

            options.UseNpgsql(
                ApplyDatabaseOptions(connectionString, databaseOptions),
                npgsqlOptions => npgsqlOptions.CommandTimeout(databaseOptions.CommandTimeoutSeconds));
        });

        return services;
    }

    // 連線字串缺漏（null／空）維持現行行為，交給 Npgsql 原有錯誤，不新增自訂例外。連線字串內已有的同名參數以
    // DatabaseOptions 為準（單一調整入口，db-timeout-pool-tuning design.md 決策 2）。
    private static string? ApplyDatabaseOptions(string? connectionString, DatabaseOptions databaseOptions)
    {
        if (string.IsNullOrEmpty(connectionString))
        {
            return connectionString;
        }

        return new NpgsqlConnectionStringBuilder(connectionString)
        {
            Timeout = databaseOptions.ConnectionTimeoutSeconds,
            MaxPoolSize = databaseOptions.MaxPoolSize,
        }.ConnectionString;
    }
}
