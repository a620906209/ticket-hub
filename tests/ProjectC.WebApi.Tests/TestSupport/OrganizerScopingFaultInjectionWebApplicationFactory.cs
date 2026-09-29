using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProjectC.Domain.Events;
using ProjectC.Domain.Orders;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Repositories;

namespace ProjectC.WebApi.Tests.TestSupport;

// order-report-redemption-organizer-scoping tasks.md 5.8／6.6 專用：真實 DB 有 FK，無法建立「訂單存在但活動
// 不存在」「票券存在但歸屬查不到」的資料，改以包裝真實 Repository 的 decorator，只對測試指定的 Id 回傳 null，
// 模擬資料毀損；其餘呼叫一律委派給真實實作。不修改 CustomWebApplicationFactory 的預設註冊。
public sealed class OrganizerScopingFaultInjectionWebApplicationFactory : CustomWebApplicationFactory
{
    public ConcurrentDictionary<Guid, byte> MissingEventIds { get; } = new();

    public ConcurrentDictionary<Guid, byte> UnresolvableOrderItemIds { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEventRepository>();
            services.AddScoped<IEventRepository>(sp =>
                new MissingEventRepository(new EventRepository(sp.GetRequiredService<ApplicationDbContext>()), MissingEventIds));

            services.RemoveAll<IOrderRepository>();
            services.AddScoped<IOrderRepository>(sp =>
                new UnresolvableOrganizerOrderRepository(new OrderRepository(sp.GetRequiredService<ApplicationDbContext>()), UnresolvableOrderItemIds));
        });
    }

    private sealed class MissingEventRepository(IEventRepository inner, ConcurrentDictionary<Guid, byte> missingEventIds) : IEventRepository
    {
        public Task<Event?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
            => missingEventIds.ContainsKey(id) ? Task.FromResult<Event?>(null) : inner.GetByIdAsync(id, cancellationToken);

        public Task<IReadOnlyList<Event>> GetAllAsync(CancellationToken cancellationToken) => inner.GetAllAsync(cancellationToken);

        public Task<IReadOnlyList<Event>> GetByOrganizerIdAsync(Guid organizerId, CancellationToken cancellationToken)
            => inner.GetByOrganizerIdAsync(organizerId, cancellationToken);

        public void Add(Event @event) => inner.Add(@event);

        public void Update(Event @event) => inner.Update(@event);

        public Task<Event?> GetForUpdateAsync(Guid eventId, CancellationToken cancellationToken) => inner.GetForUpdateAsync(eventId, cancellationToken);
    }

    private sealed class UnresolvableOrganizerOrderRepository(IOrderRepository inner, ConcurrentDictionary<Guid, byte> unresolvableOrderItemIds) : IOrderRepository
    {
        public Task<Guid?> GetOrganizerIdByOrderItemIdAsync(Guid orderItemId, CancellationToken cancellationToken)
            => unresolvableOrderItemIds.ContainsKey(orderItemId)
                ? Task.FromResult<Guid?>(null)
                : inner.GetOrganizerIdByOrderItemIdAsync(orderItemId, cancellationToken);

        public Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => inner.GetByIdAsync(id, cancellationToken);

        public Task<IReadOnlyList<Order>> GetByOrganizerIdAsync(Guid organizerId, CancellationToken cancellationToken)
            => inner.GetByOrganizerIdAsync(organizerId, cancellationToken);

        public Task<IReadOnlyList<Order>> GetByBuyerIdAsync(Guid buyerId, CancellationToken cancellationToken) => inner.GetByBuyerIdAsync(buyerId, cancellationToken);

        public Task<Order?> GetByOrderItemIdAsync(Guid orderItemId, CancellationToken cancellationToken) => inner.GetByOrderItemIdAsync(orderItemId, cancellationToken);

        public Task<IReadOnlyList<Guid>> GetExpiredPendingOrderIdsAsync(DateTime now, CancellationToken cancellationToken)
            => inner.GetExpiredPendingOrderIdsAsync(now, cancellationToken);

        public Task ReloadAsync(Order order, CancellationToken cancellationToken) => inner.ReloadAsync(order, cancellationToken);

        public void Add(Order order) => inner.Add(order);

        public Task<IReadOnlyList<OrderItemSalesGroup>> GetPaidItemSalesByEventIdAsync(Guid eventId, CancellationToken cancellationToken)
            => inner.GetPaidItemSalesByEventIdAsync(eventId, cancellationToken);
    }
}
