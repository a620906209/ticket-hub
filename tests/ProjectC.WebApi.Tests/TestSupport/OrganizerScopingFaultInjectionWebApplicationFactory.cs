using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ProjectC.Domain.Events;
using ProjectC.Domain.Members;
using ProjectC.Domain.Orders;
using ProjectC.Domain.Tickets;
using ProjectC.Domain.Venues;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Repositories;
using ProjectC.WebApi.ExceptionHandling;

namespace ProjectC.WebApi.Tests.TestSupport;

// order-report-redemption-organizer-scoping tasks.md 5.8／6.6 專用：真實 DB 有 FK，無法建立「訂單存在但活動
// 不存在」「票券存在但歸屬查不到」的資料，改以包裝真實 Repository 的 decorator，只對測試指定的 Id 回傳 null，
// 模擬資料毀損；其餘呼叫一律委派給真實實作。不修改 CustomWebApplicationFactory 的預設註冊。
// order-display-enrichment tasks.md 1.4 以同樣理由擴充座位、票種、座位範本、買家會員四種損毀。
public sealed class OrganizerScopingFaultInjectionWebApplicationFactory : CustomWebApplicationFactory
{
    public ConcurrentDictionary<Guid, byte> MissingEventIds { get; } = new();

    public ConcurrentDictionary<Guid, byte> UnresolvableOrderItemIds { get; } = new();

    public ConcurrentDictionary<Guid, byte> MissingEventSeatIds { get; } = new();

    public ConcurrentDictionary<Guid, byte> MissingTicketTypeIds { get; } = new();

    public ConcurrentDictionary<Guid, byte> MissingSeatIds { get; } = new();

    public ConcurrentDictionary<Guid, byte> MissingBuyerIds { get; } = new();

    /// <summary>取代 <see cref="GlobalExceptionHandler"/> 的 logger，供斷言資料不一致時確實記錄了帶 Id 的例外與 TraceId
    /// （order-display-enrichment tasks.md 1.8）。整個 factory 共用，斷言時須以 TraceId 篩出該請求的記錄。</summary>
    public RecordingLogger<GlobalExceptionHandler> ExceptionHandlerLogger { get; } = new();

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

            services.RemoveAll<IEventSeatRepository>();
            services.AddScoped<IEventSeatRepository>(sp =>
                new MissingEventSeatRepository(new EventSeatRepository(sp.GetRequiredService<ApplicationDbContext>()), MissingEventSeatIds));

            services.RemoveAll<ITicketTypeRepository>();
            services.AddScoped<ITicketTypeRepository>(sp =>
                new MissingTicketTypeRepository(new TicketTypeRepository(sp.GetRequiredService<ApplicationDbContext>()), MissingTicketTypeIds));

            services.RemoveAll<ISeatMapRepository>();
            services.AddScoped<ISeatMapRepository>(sp =>
                new MissingSeatRepository(new SeatMapRepository(sp.GetRequiredService<ApplicationDbContext>()), MissingSeatIds));

            services.RemoveAll<IMemberDisplayNameReader>();
            services.AddScoped<IMemberDisplayNameReader>(sp =>
                new MissingBuyerDisplayNameReader(new MemberDisplayNameReader(sp.GetRequiredService<ApplicationDbContext>()), MissingBuyerIds));

            services.RemoveAll<ILogger<GlobalExceptionHandler>>();
            services.AddSingleton<ILogger<GlobalExceptionHandler>>(ExceptionHandlerLogger);
        });
    }

    private sealed class MissingEventRepository(IEventRepository inner, ConcurrentDictionary<Guid, byte> missingEventIds) : IEventRepository
    {
        public Task<Event?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
            => missingEventIds.ContainsKey(id) ? Task.FromResult<Event?>(null) : inner.GetByIdAsync(id, cancellationToken);

        public Task<IReadOnlyList<Event>> GetAllAsync(CancellationToken cancellationToken) => inner.GetAllAsync(cancellationToken);

        public async Task<IReadOnlyList<Event>> GetByIdsAsync(IReadOnlyList<Guid> eventIds, CancellationToken cancellationToken)
            => (await inner.GetByIdsAsync(eventIds, cancellationToken)).Where(e => !missingEventIds.ContainsKey(e.Id)).ToList();

        public Task<IReadOnlyList<Event>> GetByOrganizerIdAsync(Guid organizerId, CancellationToken cancellationToken)
            => inner.GetByOrganizerIdAsync(organizerId, cancellationToken);

        public void Add(Event @event) => inner.Add(@event);

        public void Update(Event @event) => inner.Update(@event);

        public Task<Event?> GetForUpdateAsync(Guid eventId, CancellationToken cancellationToken) => inner.GetForUpdateAsync(eventId, cancellationToken);

        public Task<Event?> GetForShareAsync(Guid eventId, CancellationToken cancellationToken) => inner.GetForShareAsync(eventId, cancellationToken);
    }

    private sealed class MissingEventSeatRepository(IEventSeatRepository inner, ConcurrentDictionary<Guid, byte> missingEventSeatIds) : IEventSeatRepository
    {
        public Task<EventSeat?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => inner.GetByIdAsync(id, cancellationToken);

        public Task<IReadOnlyList<EventSeat>> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken)
            => inner.GetByEventIdAsync(eventId, cancellationToken);

        public async Task<IReadOnlyList<EventSeat>> GetByIdsAsync(IReadOnlyList<Guid> eventSeatIds, CancellationToken cancellationToken)
            => (await inner.GetByIdsAsync(eventSeatIds, cancellationToken)).Where(es => !missingEventSeatIds.ContainsKey(es.Id)).ToList();

        public Task<IReadOnlyList<EventSeat>> GetByEventIdsAsync(IReadOnlyList<Guid> eventIds, CancellationToken cancellationToken)
            => inner.GetByEventIdsAsync(eventIds, cancellationToken);

        public void AddRange(IEnumerable<EventSeat> eventSeats) => inner.AddRange(eventSeats);

        public Task<IReadOnlyList<EventSeat>> GetForUpdateAsync(IReadOnlyList<Guid> eventSeatIds, CancellationToken cancellationToken)
            => inner.GetForUpdateAsync(eventSeatIds, cancellationToken);
    }

    private sealed class MissingTicketTypeRepository(ITicketTypeRepository inner, ConcurrentDictionary<Guid, byte> missingTicketTypeIds) : ITicketTypeRepository
    {
        public Task<TicketType?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => inner.GetByIdAsync(id, cancellationToken);

        public async Task<IReadOnlyList<TicketType>> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken)
            => (await inner.GetByEventIdAsync(eventId, cancellationToken)).Where(t => !missingTicketTypeIds.ContainsKey(t.Id)).ToList();

        public void Add(TicketType ticketType) => inner.Add(ticketType);

        public Task<IReadOnlyList<TicketType>> GetForUpdateAsync(IReadOnlyList<Guid> ticketTypeIds, CancellationToken cancellationToken)
            => inner.GetForUpdateAsync(ticketTypeIds, cancellationToken);
    }

    private sealed class MissingSeatRepository(ISeatMapRepository inner, ConcurrentDictionary<Guid, byte> missingSeatIds) : ISeatMapRepository
    {
        public Task<SeatMap?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => inner.GetByIdAsync(id, cancellationToken);

        public Task<IReadOnlyList<SeatMap>> GetByVenueIdAsync(Guid venueId, CancellationToken cancellationToken)
            => inner.GetByVenueIdAsync(venueId, cancellationToken);

        public async Task<IReadOnlyList<Seat>> GetSeatsByIdsAsync(IReadOnlyList<Guid> seatIds, CancellationToken cancellationToken)
            => (await inner.GetSeatsByIdsAsync(seatIds, cancellationToken)).Where(s => !missingSeatIds.ContainsKey(s.Id)).ToList();

        public void Add(SeatMap seatMap) => inner.Add(seatMap);
    }

    private sealed class MissingBuyerDisplayNameReader(IMemberDisplayNameReader inner, ConcurrentDictionary<Guid, byte> missingBuyerIds) : IMemberDisplayNameReader
    {
        public async Task<IReadOnlyDictionary<Guid, string>> GetDisplayNamesByIdsAsync(IReadOnlyList<Guid> memberIds, CancellationToken cancellationToken)
            => (await inner.GetDisplayNamesByIdsAsync(memberIds, cancellationToken))
                .Where(pair => !missingBuyerIds.ContainsKey(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    private sealed class UnresolvableOrganizerOrderRepository(IOrderRepository inner, ConcurrentDictionary<Guid, byte> unresolvableOrderItemIds) : IOrderRepository
    {
        public Task<RedemptionContext?> GetRedemptionContextByOrderItemIdAsync(Guid orderItemId, CancellationToken cancellationToken)
            => unresolvableOrderItemIds.ContainsKey(orderItemId)
                ? Task.FromResult<RedemptionContext?>(null)
                : inner.GetRedemptionContextByOrderItemIdAsync(orderItemId, cancellationToken);

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
