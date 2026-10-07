using System.Data;
using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ProjectC.Application.Common;
using ProjectC.Application.Orders;
using ProjectC.Application.Orders.PlaceOrder;
using ProjectC.Domain.Events;
using ProjectC.Domain.Members;
using ProjectC.Domain.PurchaseQueue;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Repositories;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests;

// order-placement-p95-phase2 design.md 決策 2：高併發時每向連線池借一次連線就要重新排到隊尾，
// 所以每筆下單從第一次查詢到交易結束只能借一次，且每條結束路徑都要歸還，否則會耗盡連線池。
[Collection(PostgresCollection.Name)]
public class OrderServiceSingleConnectionTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(15);

    private readonly PostgresFixture _fixture;

    public OrderServiceSingleConnectionTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public enum PlaceOrderPath
    {
        Success,
        TicketTypeNotFound,
        CrossEvent,
        SalesNotOpen,
        RealNameRequired,
        ExceedsMaxTicketsPerOrder,
        EarlyConflict,
        ConflictAfterLock,
        QueueAdmissionRequired,
    }

    [Theory]
    [InlineData(PlaceOrderPath.Success)]
    [InlineData(PlaceOrderPath.TicketTypeNotFound)]
    [InlineData(PlaceOrderPath.CrossEvent)]
    [InlineData(PlaceOrderPath.SalesNotOpen)]
    [InlineData(PlaceOrderPath.RealNameRequired)]
    [InlineData(PlaceOrderPath.ExceedsMaxTicketsPerOrder)]
    [InlineData(PlaceOrderPath.EarlyConflict)]
    [InlineData(PlaceOrderPath.ConflictAfterLock)]
    [InlineData(PlaceOrderPath.QueueAdmissionRequired)]
    public async Task PlaceOrderAsync_OnEachResultPath_OpensConnectionOnceAndReturnsIt(PlaceOrderPath path)
    {
        // TP-ORDER-037
        var counter = new ConnectionOpenCounter();
        await using var dbContext = _fixture.CreateDbContext(counter);
        var scenario = await ArrangeAsync(path, dbContext);

        var result = await OrderServiceTestFactory.Create(dbContext, eventRepository: scenario.EventRepository)
            .PlaceOrderAsync(scenario.BuyerId, scenario.Request, CancellationToken.None);

        // 先確認真的走到預期分支，避免測試資料沒觸發該路徑卻因次數碰巧為 1 而通過。
        scenario.AssertResult(result);
        counter.OpenedCount.Should().Be(1);
        dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed);
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenExceptionThrownInsideTransaction_OpensConnectionOnceAndReturnsIt()
    {
        // TP-ORDER-037 交易內例外
        await using var seedDbContext = _fixture.CreateDbContext();
        var (eventId, eventSeatIds) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 1);
        var ticketTypeId = await TicketingTestData.SeedTicketTypeAsync(seedDbContext, eventId);
        var buyerId = await SeedBuyerAsync(seedDbContext);

        var counter = new ConnectionOpenCounter();
        await using var dbContext = _fixture.CreateDbContext(counter);
        var eventRepository = new InterceptingEventRepository(new EventRepository(dbContext))
        {
            AfterGetForShareAsync = () => throw new InvalidOperationException("Injected failure inside transaction."),
        };

        var act = () => OrderServiceTestFactory.Create(dbContext, eventRepository: eventRepository)
            .PlaceOrderAsync(buyerId, CreateRequest(ticketTypeId, eventSeatIds[0]), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Injected failure inside transaction.");
        counter.OpenedCount.Should().Be(1);
        dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed);
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenRequestFailsValidation_DoesNotOpenConnection()
    {
        // TP-ORDER-037 格式驗證失敗
        var counter = new ConnectionOpenCounter();
        await using var dbContext = _fixture.CreateDbContext(counter);

        var result = await OrderServiceTestFactory.Create(dbContext)
            .PlaceOrderAsync(Guid.NewGuid(), new PlaceOrderRequest([]), CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.Validation);
        counter.OpenedCount.Should().Be(0);
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenCancelledAfterConnectionOpenedBeforeTransaction_ReturnsConnection()
    {
        // TP-ORDER-037 取消（交易前）：連線一開啟就取消，下一個查詢拋出取消例外。
        await using var seedDbContext = _fixture.CreateDbContext();
        var (eventId, eventSeatIds) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 1);
        var ticketTypeId = await TicketingTestData.SeedTicketTypeAsync(seedDbContext, eventId);
        var buyerId = await SeedBuyerAsync(seedDbContext);

        using var cancellation = new CancellationTokenSource();
        var counter = new ConnectionOpenCounter { OnOpened = cancellation.Cancel };
        await using var dbContext = _fixture.CreateDbContext(counter);

        var act = () => OrderServiceTestFactory.Create(dbContext)
            .PlaceOrderAsync(buyerId, CreateRequest(ticketTypeId, eventSeatIds[0]), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        counter.OpenedCount.Should().Be(1);
        dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed);
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenCancelledInsideTransaction_ReturnsConnection()
    {
        // TP-ORDER-037 取消（交易內）：取得 Event 共享鎖後取消，座位 FOR UPDATE 拋出取消例外。
        await using var seedDbContext = _fixture.CreateDbContext();
        var (eventId, eventSeatIds) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 1);
        var ticketTypeId = await TicketingTestData.SeedTicketTypeAsync(seedDbContext, eventId);
        var buyerId = await SeedBuyerAsync(seedDbContext);

        using var cancellation = new CancellationTokenSource();
        var counter = new ConnectionOpenCounter();
        await using var dbContext = _fixture.CreateDbContext(counter);
        var eventRepository = new InterceptingEventRepository(new EventRepository(dbContext))
        {
            AfterGetForShareAsync = () =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            },
        };

        var act = () => OrderServiceTestFactory.Create(dbContext, eventRepository: eventRepository)
            .PlaceOrderAsync(buyerId, CreateRequest(ticketTypeId, eventSeatIds[0]), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        counter.OpenedCount.Should().Be(1);
        dbContext.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed);
        (await GetOrderItemCountForSeatAsync(eventSeatIds[0])).Should().Be(0, "取消的交易必須回滾");
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenCommittedInQueueModeWithCountingSelection_RedisCallsRunAfterConnectionReturned()
    {
        // TP-ORDER-038
        await using var seedDbContext = _fixture.CreateDbContext();
        var (eventId, _) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 1);
        var ticketTypeId = await TicketingTestData.SeedCountBasedTicketTypeAsync(seedDbContext, eventId);
        var buyerId = await SeedBuyerAsync(seedDbContext);
        await EnableQueueModeAsync(seedDbContext, eventId);
        await SeedAdmittedEntryAsync(seedDbContext, eventId, buyerId);

        await using var dbContext = _fixture.CreateDbContext();
        ConnectionState? stateDuringMirrorSync = null;
        ConnectionState? stateDuringCacheRemoval = null;
        var admissionMirror = new FakePurchaseQueueAdmissionMirror
        {
            OnSyncCompletionAsync = (_, _) =>
            {
                stateDuringMirrorSync = dbContext.Database.GetDbConnection().State;
                return Task.CompletedTask;
            },
        };
        var queryCache = new FakeQueryCache
        {
            OnRemoveAsync = _ =>
            {
                stateDuringCacheRemoval = dbContext.Database.GetDbConnection().State;
                return Task.CompletedTask;
            },
        };

        var result = await OrderServiceTestFactory.Create(dbContext, queryCache: queryCache, admissionMirror: admissionMirror)
            .PlaceOrderAsync(buyerId, new PlaceOrderRequest([new PlaceOrderSelectionRequest(null, ticketTypeId, 1)]), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        stateDuringMirrorSync.Should().Be(ConnectionState.Closed, "入場鏡像同步期間不得占用資料庫連線");
        stateDuringCacheRemoval.Should().Be(ConnectionState.Closed, "票種快取失效期間不得占用資料庫連線");
    }

    private sealed record PathScenario(
        Guid BuyerId, PlaceOrderRequest Request, Action<Result<Guid>> AssertResult, IEventRepository? EventRepository = null);

    private async Task<PathScenario> ArrangeAsync(PlaceOrderPath path, ApplicationDbContext dbContextUnderTest)
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var buyerId = await SeedBuyerAsync(seedDbContext);

        switch (path)
        {
            case PlaceOrderPath.Success:
            {
                var (eventId, eventSeatIds) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 1);
                var ticketTypeId = await TicketingTestData.SeedTicketTypeAsync(seedDbContext, eventId);
                return new PathScenario(buyerId, CreateRequest(ticketTypeId, eventSeatIds[0]), r => r.IsSuccess.Should().BeTrue());
            }
            case PlaceOrderPath.TicketTypeNotFound:
                return new PathScenario(buyerId, new PlaceOrderRequest([new PlaceOrderSelectionRequest(null, Guid.NewGuid(), 1)]),
                    r => r.Error!.Type.Should().Be(ErrorType.NotFound));
            case PlaceOrderPath.CrossEvent:
            {
                var (firstEventId, _) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 1);
                var (secondEventId, _) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 1);
                var firstTicketTypeId = await TicketingTestData.SeedCountBasedTicketTypeAsync(seedDbContext, firstEventId);
                var secondTicketTypeId = await TicketingTestData.SeedCountBasedTicketTypeAsync(seedDbContext, secondEventId);
                return new PathScenario(
                    buyerId,
                    new PlaceOrderRequest([new PlaceOrderSelectionRequest(null, firstTicketTypeId, 1), new PlaceOrderSelectionRequest(null, secondTicketTypeId, 1)]),
                    r => AssertError(r, ErrorType.Validation, "same event"));
            }
            case PlaceOrderPath.SalesNotOpen:
            {
                var (eventId, eventSeatIds) = await TicketingTestData.SeedEventWithSeatsAsync(
                    seedDbContext, seatCount: 1, salesStartAtUtc: DateTime.UtcNow.AddDays(1));
                var ticketTypeId = await TicketingTestData.SeedTicketTypeAsync(seedDbContext, eventId);
                return new PathScenario(buyerId, CreateRequest(ticketTypeId, eventSeatIds[0]), r => r.Error!.Type.Should().Be(ErrorType.SalesNotOpen));
            }
            case PlaceOrderPath.RealNameRequired:
            {
                var (eventId, eventSeatIds) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 1, isRealNameRequired: true);
                var ticketTypeId = await TicketingTestData.SeedTicketTypeAsync(seedDbContext, eventId);
                return new PathScenario(buyerId, CreateRequest(ticketTypeId, eventSeatIds[0]), r => r.Error!.Type.Should().Be(ErrorType.RealNameRequired));
            }
            case PlaceOrderPath.ExceedsMaxTicketsPerOrder:
            {
                var (eventId, _) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 1, maxTicketsPerOrder: 2);
                var ticketTypeId = await TicketingTestData.SeedCountBasedTicketTypeAsync(seedDbContext, eventId);
                return new PathScenario(buyerId, new PlaceOrderRequest([new PlaceOrderSelectionRequest(null, ticketTypeId, 3)]),
                    r => AssertError(r, ErrorType.Validation, "at most 2"));
            }
            case PlaceOrderPath.EarlyConflict:
            {
                var (eventId, eventSeatIds) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 1);
                var ticketTypeId = await TicketingTestData.SeedTicketTypeAsync(seedDbContext, eventId);
                await PlaceOrderWithSeparateContextAsync(ticketTypeId, eventSeatIds[0]);
                return new PathScenario(buyerId, CreateRequest(ticketTypeId, eventSeatIds[0]),
                    r => r.Error.Should().BeEquivalentTo(PlaceOrderConflictErrors.SeatNoLongerAvailable(eventSeatIds[0])));
            }
            case PlaceOrderPath.ConflictAfterLock:
            {
                // 交易外讀到座位可售、通過提早判斷；取得 Event 共享鎖後另一筆訂單先買走，鎖內判斷才回 409。
                var (eventId, eventSeatIds) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 1);
                var ticketTypeId = await TicketingTestData.SeedTicketTypeAsync(seedDbContext, eventId);
                var eventRepository = new InterceptingEventRepository(new EventRepository(dbContextUnderTest))
                {
                    AfterGetForShareAsync = () => PlaceOrderWithSeparateContextAsync(ticketTypeId, eventSeatIds[0]).WaitAsync(WaitTimeout),
                };
                return new PathScenario(buyerId, CreateRequest(ticketTypeId, eventSeatIds[0]),
                    r => r.Error.Should().BeEquivalentTo(PlaceOrderConflictErrors.SeatNoLongerAvailable(eventSeatIds[0])), eventRepository);
            }
            case PlaceOrderPath.QueueAdmissionRequired:
            {
                var (eventId, eventSeatIds) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 1);
                var ticketTypeId = await TicketingTestData.SeedTicketTypeAsync(seedDbContext, eventId);
                await EnableQueueModeAsync(seedDbContext, eventId);
                return new PathScenario(buyerId, CreateRequest(ticketTypeId, eventSeatIds[0]),
                    r => r.Error!.Type.Should().Be(ErrorType.QueueAdmissionRequired));
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(path), path, null);
        }
    }

    private static void AssertError(Result<Guid> result, ErrorType expectedType, string expectedMessagePart)
    {
        result.Error!.Type.Should().Be(expectedType);
        result.Error.Message.Should().Contain(expectedMessagePart);
    }

    private async Task PlaceOrderWithSeparateContextAsync(Guid ticketTypeId, Guid eventSeatId)
    {
        await using var otherDbContext = _fixture.CreateDbContext();
        var otherBuyerId = await SeedBuyerAsync(otherDbContext);
        var result = await OrderServiceTestFactory.Create(otherDbContext)
            .PlaceOrderAsync(otherBuyerId, CreateRequest(ticketTypeId, eventSeatId), CancellationToken.None);
        result.IsSuccess.Should().BeTrue("前置訂單必須先占住座位，被測請求才會走到 409 分支");
    }

    private static PlaceOrderRequest CreateRequest(Guid ticketTypeId, Guid eventSeatId)
        => new([new PlaceOrderSelectionRequest(eventSeatId, ticketTypeId)]);

    private static async Task EnableQueueModeAsync(ApplicationDbContext dbContext, Guid eventId)
    {
        var @event = await dbContext.Events.SingleAsync(e => e.Id == eventId);
        @event.EnableQueueMode();
        await dbContext.SaveChangesAsync();
    }

    private static async Task SeedAdmittedEntryAsync(ApplicationDbContext dbContext, Guid eventId, Guid buyerId)
    {
        var now = DateTime.UtcNow;
        var entry = new PurchaseQueueEntry(Guid.NewGuid(), eventId, buyerId, now.AddMinutes(-5));
        entry.Admit(now.AddMinutes(-1), now.AddMinutes(10));
        dbContext.PurchaseQueueEntries.Add(entry);
        await dbContext.SaveChangesAsync();
    }

    private async Task<int> GetOrderItemCountForSeatAsync(Guid eventSeatId)
    {
        await using var readDbContext = _fixture.CreateDbContext();
        return await readDbContext.OrderItems.CountAsync(i => i.EventSeatId == eventSeatId);
    }

    private static async Task<Guid> SeedBuyerAsync(ApplicationDbContext dbContext)
    {
        var buyer = Member.Register($"buyer-{Guid.NewGuid():N}@example.com", "Test Buyer", "hash");
        dbContext.Members.Add(buyer);
        await dbContext.SaveChangesAsync();
        return buyer.Id;
    }

    private sealed class ConnectionOpenCounter : DbConnectionInterceptor
    {
        private int _openedCount;

        public int OpenedCount => Volatile.Read(ref _openedCount);

        public Action? OnOpened { get; init; }

        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        {
            Interlocked.Increment(ref _openedCount);
            OnOpened?.Invoke();
        }

        public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _openedCount);
            OnOpened?.Invoke();
            return Task.CompletedTask;
        }
    }
}
