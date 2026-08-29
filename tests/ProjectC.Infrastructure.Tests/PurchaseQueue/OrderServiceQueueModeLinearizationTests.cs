using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProjectC.Application.Common;
using ProjectC.Application.Orders.PlaceOrder;
using ProjectC.Domain.Events;
using ProjectC.Domain.Members;
using ProjectC.Domain.Venues;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Repositories;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests.PurchaseQueue;

// 驗證 rate-limiting-queue design.md 決策 4「Queue Mode 切換的線性化時點」與 tasks.md 1.5
// （EventRepository.GetByIdAsync 改 AsNoTracking）本身——若 1.5 沒有正確實作，這裡的測試 MUST 會失敗
// （讀到交易前的舊值），可作為驗收 1.5 是否確實生效的直接手段（見 tasks.md 12.9 TP-ORDER-015／016）。
[Collection(PostgresCollection.Name)]
public class OrderServiceQueueModeLinearizationTests
{
    private readonly PostgresFixture _fixture;

    public OrderServiceQueueModeLinearizationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>包住真正的 EventRepository，在第一次 GetByIdAsync 回傳「之後」（模擬「交易前的讀取已發生」）
    /// 觸發一次由呼叫端指定的併發寫入，藉此在不修改 OrderService 本身的情況下精確控制交錯時機。</summary>
    private sealed class GetByIdInterceptingEventRepository : IEventRepository
    {
        private readonly IEventRepository _inner;
        private readonly Func<Task> _onFirstGetByIdAsync;
        private int _triggered;

        public GetByIdInterceptingEventRepository(IEventRepository inner, Func<Task> onFirstGetByIdAsync)
        {
            _inner = inner;
            _onFirstGetByIdAsync = onFirstGetByIdAsync;
        }

        public async Task<Event?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        {
            var result = await _inner.GetByIdAsync(id, cancellationToken);
            if (Interlocked.Exchange(ref _triggered, 1) == 0)
            {
                await _onFirstGetByIdAsync();
            }

            return result;
        }

        public Task<IReadOnlyList<Event>> GetAllAsync(CancellationToken cancellationToken) => _inner.GetAllAsync(cancellationToken);

        public void Add(Event @event) => _inner.Add(@event);

        public void Update(Event @event) => _inner.Update(@event);

        public Task<Event?> GetForUpdateAsync(Guid eventId, CancellationToken cancellationToken) => _inner.GetForUpdateAsync(eventId, cancellationToken);
    }

    /// <summary>純計數票種，確保 OrderService.PlaceOrderAsync 只呼叫一次 GetByIdAsync（座位選購迴圈裡的
    /// 第二次呼叫只在有座位項目時才會觸發），交錯時機才能精確對應「交易前的唯一一次讀取」。</summary>
    private async Task<(Guid EventId, Guid TicketTypeId, Guid BuyerId)> SeedCountBasedEventAsync(
        ApplicationDbContext dbContext, bool isQueueModeEnabledInitially)
    {
        var venue = new Venue(Guid.NewGuid(), $"Test Venue {Guid.NewGuid():N}");
        var seatMap = new SeatMap(Guid.NewGuid(), venue.Id);
        var @event = new Event(Guid.NewGuid(), "Test Event", DateTime.UtcNow.AddDays(30), venue.Id, seatMap.Id);
        if (isQueueModeEnabledInitially)
        {
            @event.EnableQueueMode();
        }

        var ticketType = @event.CreateCountBasedTicketType("站票", 300m, 10);
        var buyer = Member.Register($"buyer-{Guid.NewGuid():N}@example.com", "Test Buyer", "hash");

        dbContext.Venues.Add(venue);
        dbContext.SeatMaps.Add(seatMap);
        dbContext.Events.Add(@event);
        dbContext.TicketTypes.Add(ticketType);
        dbContext.Members.Add(buyer);
        await dbContext.SaveChangesAsync();

        return (@event.Id, ticketType.Id, buyer.Id);
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenQueueModeIsEnabledByAdminDuringProcessing_RejectsUsingTheLatestValueNotTheStaleReadBeforeTheTransaction()
    {
        // TP-ORDER-015：買家送出請求時活動仍是 false，但 Admin 在系統實際執行建立邏輯之前切換為 true，
        // 且買家不具備已入場的排隊資格——系統 MUST 以切換後的最新值為準，拒絕建立訂單。
        await using var seedDbContext = _fixture.CreateDbContext();
        var (eventId, ticketTypeId, buyerId) = await SeedCountBasedEventAsync(seedDbContext, isQueueModeEnabledInitially: false);

        await using var dbContext = _fixture.CreateDbContext();
        var interceptingEventRepository = new GetByIdInterceptingEventRepository(new EventRepository(dbContext), async () =>
        {
            await using var writerDbContext = _fixture.CreateDbContext();
            var eventEntity = await writerDbContext.Events.SingleAsync(e => e.Id == eventId);
            eventEntity.EnableQueueMode();
            await writerDbContext.SaveChangesAsync();
        });
        var orderService = OrderServiceTestFactory.Create(dbContext, eventRepository: interceptingEventRepository);
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(null, ticketTypeId, 1)]);

        var result = await orderService.PlaceOrderAsync(buyerId, request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.QueueAdmissionRequired,
            "1.5 若未正確實作為 AsNoTracking，這裡會讀到交易前的舊快照（false）而誤判成功");

        await using var readDbContext = _fixture.CreateDbContext();
        var ticketType = await readDbContext.TicketTypes.AsNoTracking().SingleAsync(t => t.Id == ticketTypeId);
        ticketType.AvailableQuantity.Should().Be(10, "被拒絕的訂單不應該扣減任何庫存");
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenQueueModeIsDisabledByAdminDuringProcessing_SucceedsUsingTheLatestValueNotTheStaleReadBeforeTheTransaction()
    {
        // TP-ORDER-016：買家送出請求時活動仍是 true 且買家不具備已入場資格，但 Admin 在系統實際執行建立
        // 邏輯之前切換為 false——系統 MUST 以切換後的最新值為準，不再檢查排隊資格，正常處理建立訂單。
        await using var seedDbContext = _fixture.CreateDbContext();
        var (eventId, ticketTypeId, buyerId) = await SeedCountBasedEventAsync(seedDbContext, isQueueModeEnabledInitially: true);

        await using var dbContext = _fixture.CreateDbContext();
        var interceptingEventRepository = new GetByIdInterceptingEventRepository(new EventRepository(dbContext), async () =>
        {
            await using var writerDbContext = _fixture.CreateDbContext();
            var eventEntity = await writerDbContext.Events.SingleAsync(e => e.Id == eventId);
            eventEntity.DisableQueueMode();
            await writerDbContext.SaveChangesAsync();
        });
        var orderService = OrderServiceTestFactory.Create(dbContext, eventRepository: interceptingEventRepository);
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(null, ticketTypeId, 1)]);

        var result = await orderService.PlaceOrderAsync(buyerId, request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(
            "1.5 若未正確實作為 AsNoTracking，這裡會讀到交易前的舊快照（true）並在買家無排隊資格時誤判失敗");

        await using var readDbContext = _fixture.CreateDbContext();
        var ticketType = await readDbContext.TicketTypes.AsNoTracking().SingleAsync(t => t.Id == ticketTypeId);
        ticketType.AvailableQuantity.Should().Be(9, "訂單成功應該扣減庫存");
    }
}
