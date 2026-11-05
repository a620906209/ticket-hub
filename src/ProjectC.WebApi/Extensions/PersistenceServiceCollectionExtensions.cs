using Microsoft.EntityFrameworkCore;
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
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("DefaultConnection")));
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
}
