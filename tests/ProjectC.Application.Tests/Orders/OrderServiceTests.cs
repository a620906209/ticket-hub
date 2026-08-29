using FluentAssertions;
using ProjectC.Application.Common;
using ProjectC.Application.Orders;
using ProjectC.Application.Orders.PlaceOrder;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Events;
using ProjectC.Domain.Orders;
using ProjectC.Domain.Payments;
using ProjectC.Domain.PurchaseQueue;
using ProjectC.Domain.Tickets;
using ProjectC.Domain.Venues;

namespace ProjectC.Application.Tests.Orders;

public class OrderServiceTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    private sealed class Fixture
    {
        public FakeEventRepository EventRepository { get; } = new();
        public FakeEventSeatRepository EventSeatRepository { get; } = new();
        public FakeSeatMapRepository SeatMapRepository { get; } = new();
        public FakeTicketTypeRepository TicketTypeRepository { get; } = new();
        public FakeOrderRepository OrderRepository { get; } = new();
        public FakePurchaseQueueRepository PurchaseQueueRepository { get; } = new();
        public FakeTicketRepository TicketRepository { get; } = new();
        public FakeUnitOfWork UnitOfWork { get; } = new();
        public FakeDateTimeProvider DateTimeProvider { get; } = new() { UtcNow = Now };
        public FakePaymentGateway PaymentGateway { get; } = new(PaymentResult.Succeeded);

        public OrderService CreateOrderService() => new(
            TicketTypeRepository,
            EventSeatRepository,
            EventRepository,
            SeatMapRepository,
            OrderRepository,
            PurchaseQueueRepository,
            UnitOfWork,
            new PlaceOrderRequestValidator(),
            DateTimeProvider,
            new CreateOrderHandler(DateTimeProvider),
            new ConfirmOrderHandler(DateTimeProvider, PaymentGateway, TicketRepository),
            new CancelOrderHandler(DateTimeProvider));

        public (Event Event, SeatMap SeatMap, EventSeat EventSeat, TicketType TicketType) SeedEventWithSeatAndTicketType(
            string seatZoneCode = "A", string ticketTypeZoneCode = "A")
        {
            var seatMap = new SeatMap(Guid.NewGuid(), Guid.NewGuid());
            var seat = seatMap.AddSeat(seatZoneCode, "1");
            var @event = new Event(Guid.NewGuid(), "Concert", Now.AddDays(1), Guid.NewGuid(), seatMap.Id);
            var eventSeat = @event.CreateEventSeats(seatMap).Single(s => s.SeatId == seat.Id);

            if (ticketTypeZoneCode != seatZoneCode)
                seatMap.AddSeat(ticketTypeZoneCode, "2");
            var ticketType = @event.CreateTicketType(ticketTypeZoneCode, 500m, seatMap);

            EventRepository.Data.Add(@event);
            SeatMapRepository.Data.Add(seatMap);
            EventSeatRepository.Data.Add(eventSeat);
            TicketTypeRepository.Data.Add(ticketType);

            return (@event, seatMap, eventSeat, ticketType);
        }

        public (Event Event, TicketType TicketType, List<EventSeat> EventSeats) SeedEventWithMultipleSeats(
            int seatCount, int? maxTicketsPerOrder, string zoneCode = "A")
        {
            var seatMap = new SeatMap(Guid.NewGuid(), Guid.NewGuid());
            var seatTemplates = Enumerable.Range(1, seatCount).Select(n => seatMap.AddSeat(zoneCode, n.ToString())).ToList();
            var @event = new Event(
                Guid.NewGuid(), "Concert", Now.AddDays(1), Guid.NewGuid(), seatMap.Id, maxTicketsPerOrder: maxTicketsPerOrder);
            var eventSeats = @event.CreateEventSeats(seatMap).ToList();
            var ticketType = @event.CreateTicketType(zoneCode, 500m, seatMap);

            EventRepository.Data.Add(@event);
            SeatMapRepository.Data.Add(seatMap);
            EventSeatRepository.Data.AddRange(eventSeats);
            TicketTypeRepository.Data.Add(ticketType);

            return (@event, ticketType, eventSeats);
        }

        public (Event Event, TicketType CountTicketType) SeedEventWithCountBasedTicketType(
            int availableQuantity = 10, int? maxTicketsPerOrder = null, decimal price = 300m)
        {
            var seatMap = new SeatMap(Guid.NewGuid(), Guid.NewGuid());
            var @event = new Event(
                Guid.NewGuid(), "Concert", Now.AddDays(1), Guid.NewGuid(), seatMap.Id, maxTicketsPerOrder: maxTicketsPerOrder);
            var ticketType = @event.CreateCountBasedTicketType("站票", price, availableQuantity);

            EventRepository.Data.Add(@event);
            SeatMapRepository.Data.Add(seatMap);
            TicketTypeRepository.Data.Add(ticketType);

            return (@event, ticketType);
        }
    }

    // ---- PlaceOrderAsync ----

    [Fact]
    public async Task PlaceOrderAsync_WithValidSeatAndMatchingZoneTicketType_CreatesOrderAndCommits()
    {
        var fixture = new Fixture();
        var (_, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType();
        var buyerId = Guid.NewGuid();
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeat.Id, ticketType.Id)]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(buyerId, request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        fixture.OrderRepository.Data.Should().ContainSingle(o => o.Id == result.Value && o.BuyerId == buyerId);
        fixture.UnitOfWork.LastTransaction!.Committed.Should().BeTrue();
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenTicketTypeDoesNotExist_ReturnsNotFound()
    {
        var fixture = new Fixture();
        var (_, _, eventSeat, _) = fixture.SeedEventWithSeatAndTicketType();
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeat.Id, Guid.NewGuid())]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenSeatDoesNotExist_ReturnsNotFound()
    {
        var fixture = new Fixture();
        var (_, _, _, ticketType) = fixture.SeedEventWithSeatAndTicketType();
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(Guid.NewGuid(), ticketType.Id)]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenSeatZoneDoesNotMatchTicketTypeZone_ReturnsValidationError()
    {
        var fixture = new Fixture();
        var (_, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType(seatZoneCode: "A", ticketTypeZoneCode: "B");
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeat.Id, ticketType.Id)]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        fixture.OrderRepository.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenSelectionsExceedEventMaxTicketsPerOrder_ReturnsValidationErrorAndDoesNotCreateOrder()
    {
        var fixture = new Fixture();
        var (_, ticketType, eventSeats) = fixture.SeedEventWithMultipleSeats(seatCount: 3, maxTicketsPerOrder: 2);
        var request = new PlaceOrderRequest(eventSeats
            .Select(seat => new PlaceOrderSelectionRequest(seat.Id, ticketType.Id))
            .ToList());

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        fixture.OrderRepository.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenSelectionsAtEventMaxTicketsPerOrder_Succeeds()
    {
        var fixture = new Fixture();
        var (_, ticketType, eventSeats) = fixture.SeedEventWithMultipleSeats(seatCount: 3, maxTicketsPerOrder: 2);
        var request = new PlaceOrderRequest(eventSeats
            .Take(2)
            .Select(seat => new PlaceOrderSelectionRequest(seat.Id, ticketType.Id))
            .ToList());

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        fixture.OrderRepository.Data.Should().ContainSingle(o => o.Id == result.Value);
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenEventHasNoMaxTicketsPerOrder_AllowsAnySelectionCount()
    {
        var fixture = new Fixture();
        var (_, ticketType, eventSeats) = fixture.SeedEventWithMultipleSeats(seatCount: 3, maxTicketsPerOrder: null);
        var request = new PlaceOrderRequest(eventSeats
            .Select(seat => new PlaceOrderSelectionRequest(seat.Id, ticketType.Id))
            .ToList());

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    // ---- 純計數（不綁座位）選購 ----

    [Fact]
    public async Task PlaceOrderAsync_WithPureCountingSelection_CreatesOrderAndReducesAvailableQuantity()
    {
        var fixture = new Fixture();
        var (_, ticketType) = fixture.SeedEventWithCountBasedTicketType(availableQuantity: 10);
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(null, ticketType.Id, 3)]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var order = fixture.OrderRepository.Data.Single(o => o.Id == result.Value);
        order.Items.Should().ContainSingle(i => i.TicketTypeId == ticketType.Id && i.EventSeatId == null && i.Quantity == 3);
        ticketType.AvailableQuantity.Should().Be(7);
    }

    [Fact]
    public async Task PlaceOrderAsync_WithPureCountingSelectionExceedingAvailableQuantity_ReturnsConflictAndDoesNotReduceQuantity()
    {
        var fixture = new Fixture();
        var (_, ticketType) = fixture.SeedEventWithCountBasedTicketType(availableQuantity: 2);
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(null, ticketType.Id, 3)]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Conflict);
        fixture.OrderRepository.Data.Should().BeEmpty();
        ticketType.AvailableQuantity.Should().Be(2);
    }

    [Fact]
    public async Task PlaceOrderAsync_WithMixedSeatAndCountingSelections_CreatesSingleOrderWithBothItemShapes()
    {
        var fixture = new Fixture();
        var (_, _, eventSeat, seatTicketType) = fixture.SeedEventWithSeatAndTicketType();
        var (_, countTicketType) = fixture.SeedEventWithCountBasedTicketType(availableQuantity: 5);
        // 混合訂單須同一場活動：把計數票種掛到座位所屬的活動上。
        var mixedTicketType = fixture.EventRepository.Data
            .Single(e => e.Id == eventSeat.EventId)
            .CreateCountBasedTicketType("站票", 300m, 5);
        fixture.TicketTypeRepository.Data.Add(mixedTicketType);

        var request = new PlaceOrderRequest([
            new PlaceOrderSelectionRequest(eventSeat.Id, seatTicketType.Id),
            new PlaceOrderSelectionRequest(null, mixedTicketType.Id, 2)
        ]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var order = fixture.OrderRepository.Data.Single(o => o.Id == result.Value);
        order.Items.Should().HaveCount(2);
        order.Items.Should().ContainSingle(i => i.EventSeatId == eventSeat.Id && i.Quantity == 1);
        order.Items.Should().ContainSingle(i => i.EventSeatId == null && i.Quantity == 2);
        mixedTicketType.AvailableQuantity.Should().Be(3);
        _ = countTicketType; // 只用於確認上面 mixedTicketType 是另外新增的票種，不影響這個未使用的票種。
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenCountingSelectionsSpanDifferentEvents_ReturnsValidationErrorBeforeTakingAnyLock()
    {
        // 外部審查抓到：跨活動驗證 MUST 在取得任何資料庫鎖之前完成（ticket-ordering spec）。
        // 用 BeginTransactionCallCount 直接證明：驗證失敗時交易根本沒開始，鎖也就不可能被取得。
        var fixture = new Fixture();
        var (_, ticketTypeA) = fixture.SeedEventWithCountBasedTicketType();
        var (_, ticketTypeB) = fixture.SeedEventWithCountBasedTicketType();
        var request = new PlaceOrderRequest([
            new PlaceOrderSelectionRequest(null, ticketTypeA.Id, 1),
            new PlaceOrderSelectionRequest(null, ticketTypeB.Id, 1)
        ]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        fixture.OrderRepository.Data.Should().BeEmpty();
        fixture.UnitOfWork.BeginTransactionCallCount.Should().Be(0, "跨活動驗證失敗時不應該開始交易，更不該取得任何鎖");
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenSeatAndItsPairedTicketTypeBelongToDifferentEvents_ReturnsValidationErrorBeforeTakingAnyLock()
    {
        // 外部審查第二輪抓到：先前的修正只把 ticketTypesById 的 EventId 拿來比對，只有「一個票種」時
        // （這裡的重現情境）不會偵測到「座位跟它配對的票種其實屬於不同活動」，直到座位被鎖定後才被
        // 後面的 ticketType.EventId != eventSeat.EventId 擋下。座位所屬活動這次也要納入鎖定前的比對集合。
        var fixture = new Fixture();
        var (_, _, eventSeatFromEventA, _) = fixture.SeedEventWithSeatAndTicketType();
        var (_, _, _, ticketTypeFromEventB) = fixture.SeedEventWithSeatAndTicketType();
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeatFromEventA.Id, ticketTypeFromEventB.Id)]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        fixture.OrderRepository.Data.Should().BeEmpty();
        fixture.UnitOfWork.BeginTransactionCallCount.Should().Be(0, "座位跟票種不同活動時不應該開始交易，更不該取得任何鎖");
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenTotalQuantityWouldOverflowInt32_ReturnsValidationErrorInsteadOfThrowing()
    {
        // 外部審查抓到：Quantity 是外部輸入，validator 只保證 >= 1、沒有上限；限購檢查若用 int 累加
        // 兩個接近 int.MaxValue 的 Quantity 會拋 OverflowException、變成未預期的 500 而非驗證錯誤。
        // 必須用兩個「不同」計數票種各自送出接近 int.MaxValue 的 Quantity——單一 int.MaxValue 本身不會讓
        // 舊版 Sum(int) 溢位（int.MaxValue 本身是合法的 int），且同一票種重複出現會先被 validator 擋下，
        // 測不出真正的加總溢位（外部審查第二輪抓到，原本的測試只送一筆，測不出修正前的缺陷）。
        var fixture = new Fixture();
        var (event1, ticketTypeA) = fixture.SeedEventWithCountBasedTicketType(availableQuantity: 10, maxTicketsPerOrder: 4);
        var ticketTypeB = event1.CreateCountBasedTicketType("停車票", 200m, 10);
        fixture.TicketTypeRepository.Data.Add(ticketTypeB);
        var request = new PlaceOrderRequest([
            new PlaceOrderSelectionRequest(null, ticketTypeA.Id, int.MaxValue),
            new PlaceOrderSelectionRequest(null, ticketTypeB.Id, int.MaxValue)
        ]);

        var act = async () => await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        var result = await act.Should().NotThrowAsync("超大 Quantity 加總應該被限購檢查擋下，不應該讓 Sum 溢位拋例外");
        result.Subject.IsSuccess.Should().BeFalse();
        result.Subject.Error!.Type.Should().Be(ErrorType.Validation);
        fixture.OrderRepository.Data.Should().BeEmpty();
        ticketTypeA.AvailableQuantity.Should().Be(10);
        ticketTypeB.AvailableQuantity.Should().Be(10);
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenCountingTicketTypeSpecifiesEventSeatId_ReturnsValidationError()
    {
        var fixture = new Fixture();
        var (_, _, eventSeat, _) = fixture.SeedEventWithSeatAndTicketType();
        var (_, countTicketType) = fixture.SeedEventWithCountBasedTicketType();
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeat.Id, countTicketType.Id, 1)]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        fixture.OrderRepository.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenSeatTicketTypeDoesNotSpecifyEventSeatId_ReturnsValidationError()
    {
        var fixture = new Fixture();
        var (_, _, _, seatTicketType) = fixture.SeedEventWithSeatAndTicketType();
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(null, seatTicketType.Id, 1)]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        fixture.OrderRepository.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenSeatItemSpecifiesQuantityOtherThanOne_ReturnsValidationError()
    {
        var fixture = new Fixture();
        var (_, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType();
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeat.Id, ticketType.Id, 2)]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        fixture.OrderRepository.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenSameCountingTicketTypeAppearsTwice_ReturnsValidationError()
    {
        var fixture = new Fixture();
        var (_, ticketType) = fixture.SeedEventWithCountBasedTicketType(availableQuantity: 10);
        var request = new PlaceOrderRequest([
            new PlaceOrderSelectionRequest(null, ticketType.Id, 2),
            new PlaceOrderSelectionRequest(null, ticketType.Id, 3)
        ]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        fixture.OrderRepository.Data.Should().BeEmpty();
        ticketType.AvailableQuantity.Should().Be(10);
    }

    [Fact]
    public async Task PlaceOrderAsync_WithPureCountingSelectionExceedingMaxTicketsPerOrder_ReturnsValidationError()
    {
        var fixture = new Fixture();
        var (_, ticketType) = fixture.SeedEventWithCountBasedTicketType(availableQuantity: 10, maxTicketsPerOrder: 4);
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(null, ticketType.Id, 5)]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        fixture.OrderRepository.Data.Should().BeEmpty();
        ticketType.AvailableQuantity.Should().Be(10);
    }

    [Fact]
    public async Task PlaceOrderAsync_WithMixedSelectionsQuantitySumAtMaxTicketsPerOrder_Succeeds()
    {
        var fixture = new Fixture();
        var (_, _, eventSeat, seatTicketType) = fixture.SeedEventWithSeatAndTicketType();
        var @event = fixture.EventRepository.Data.Single(e => e.Id == eventSeat.EventId);
        // 限購上限要掛在同一場活動上，重新建一個帶 maxTicketsPerOrder 的活動並搬移既有的座位/票種資料。
        var eventWithLimit = new Event(@event.Id, @event.Title, @event.StartAtUtc, @event.VenueId, @event.SeatMapId, maxTicketsPerOrder: 3);
        fixture.EventRepository.Data.Remove(@event);
        fixture.EventRepository.Data.Add(eventWithLimit);
        var countTicketType = eventWithLimit.CreateCountBasedTicketType("站票", 300m, 5);
        fixture.TicketTypeRepository.Data.Add(countTicketType);

        var request = new PlaceOrderRequest([
            new PlaceOrderSelectionRequest(eventSeat.Id, seatTicketType.Id),
            new PlaceOrderSelectionRequest(null, countTicketType.Id, 2)
        ]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    // ---- 熱門搶購模式排隊資格檢查（rate-limiting-queue design.md 決策 4，ticket-purchase spec TP-ORDER-011~014） ----

    [Fact]
    public async Task PlaceOrderAsync_WhenQueueModeEnabledAndCallerIsAdmittedAndNotExpired_SucceedsAndCompletesQueueEntry()
    {
        var fixture = new Fixture();
        var (@event, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType();
        @event.EnableQueueMode();
        var buyerId = Guid.NewGuid();
        var entry = new PurchaseQueueEntry(Guid.NewGuid(), @event.Id, buyerId, Now.AddMinutes(-10));
        entry.Admit(Now.AddMinutes(-5), Now.AddMinutes(5));
        fixture.PurchaseQueueRepository.Data.Add(entry);
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeat.Id, ticketType.Id)]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(buyerId, request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        fixture.OrderRepository.Data.Should().ContainSingle(o => o.Id == result.Value);
        // PQ-COMPLETE-001：同一交易內將排隊紀錄標記為 Completed，名額即時釋放。
        entry.Status.Should().Be(PurchaseQueueEntryStatus.Completed);
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenQueueModeEnabledAndCallerHasNoAdmission_ReturnsQueueAdmissionRequiredAndDoesNotLockAnything()
    {
        var fixture = new Fixture();
        var (@event, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType();
        @event.EnableQueueMode();
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeat.Id, ticketType.Id)]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.QueueAdmissionRequired);
        fixture.OrderRepository.Data.Should().BeEmpty();
        eventSeat.GetStatus(fixture.DateTimeProvider.UtcNow).Should().Be(EventSeatStatus.Available, "未取得入場資格時不應鎖定任何座位");
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenQueueModeDisabled_SucceedsWithoutCheckingQueueEntry()
    {
        var fixture = new Fixture();
        var (@event, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType();
        // @event.IsQueueModeEnabled 預設為 false，不呼叫 EnableQueueMode()。
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeat.Id, ticketType.Id)]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _ = @event;
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenAdmissionExpiresExactlyAtCheckTime_ReturnsQueueAdmissionRequiredAndDoesNotExpireEntry()
    {
        // TP-ORDER-014：即使請求送出當下資格仍有效，仍以系統檢查當下的最新狀態為準；
        // OrderService MUST NOT 呼叫 Expire()——落地寫入統一交由背景服務／自我修復流程負責（design.md 決策 4）。
        var fixture = new Fixture();
        var (@event, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType();
        @event.EnableQueueMode();
        var buyerId = Guid.NewGuid();
        var entry = new PurchaseQueueEntry(Guid.NewGuid(), @event.Id, buyerId, Now.AddMinutes(-10));
        entry.Admit(Now.AddMinutes(-5), Now);
        fixture.PurchaseQueueRepository.Data.Add(entry);
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeat.Id, ticketType.Id)]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(buyerId, request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.QueueAdmissionRequired);
        entry.Status.Should().Be(PurchaseQueueEntryStatus.Admitted, "OrderService 只讀取判斷，不落地寫入 Expire()");
        fixture.OrderRepository.Data.Should().BeEmpty();
    }

    // ---- ConfirmOrderAsync / CancelOrderAsync ----

    private async Task<(Fixture Fixture, Order Order, Guid BuyerId)> PlaceOrderAsync(Fixture fixture)
    {
        var (_, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType();
        var buyerId = Guid.NewGuid();
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeat.Id, ticketType.Id)]);
        var result = await fixture.CreateOrderService().PlaceOrderAsync(buyerId, request, CancellationToken.None);
        return (fixture, fixture.OrderRepository.Data.Single(o => o.Id == result.Value), buyerId);
    }

    [Fact]
    public async Task ConfirmOrderAsync_WhenBuyerConfirmsOwnPendingOrder_Succeeds()
    {
        var (fixture, order, buyerId) = await PlaceOrderAsync(new Fixture());

        var result = await fixture.CreateOrderService().ConfirmOrderAsync(order.Id, buyerId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Paid);

        // ticket-purchase spec「買家確認自己的訂單成功」：確認成功後依訂單項目購買數量建立對應張數、
        // 狀態皆為 Issued 的 Ticket（與 ticket-issuance 能力共用同一段出票邏輯，見 ConfirmOrderHandler）。
        var expectedTicketCount = order.Items.Sum(i => i.Quantity);
        fixture.TicketRepository.Data.Should().HaveCount(expectedTicketCount);
        fixture.TicketRepository.Data.Should().OnlyContain(t => t.Status == TicketStatus.Issued);
        fixture.TicketRepository.Data.Should().OnlyContain(t => order.Items.Select(i => i.Id).Contains(t.OrderItemId));
    }

    [Fact]
    public async Task CancelOrderAsync_WhenBuyerCancelsOwnPendingOrder_Succeeds()
    {
        var (fixture, order, buyerId) = await PlaceOrderAsync(new Fixture());

        var result = await fixture.CreateOrderService().CancelOrderAsync(order.Id, buyerId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Cancelled);
    }

    [Fact]
    public async Task ConfirmOrderAsync_WhenCallerIsNotTheBuyer_ReturnsForbiddenAndDoesNotChangeOrder()
    {
        var (fixture, order, _) = await PlaceOrderAsync(new Fixture());

        var result = await fixture.CreateOrderService().ConfirmOrderAsync(order.Id, Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Forbidden);
        order.Status.Should().Be(OrderStatus.Pending);
        fixture.PaymentGateway.CallCount.Should().Be(0);
        fixture.TicketRepository.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task CancelOrderAsync_WhenCallerIsNotTheBuyer_ReturnsForbiddenAndDoesNotChangeOrder()
    {
        var (fixture, order, _) = await PlaceOrderAsync(new Fixture());

        var result = await fixture.CreateOrderService().CancelOrderAsync(order.Id, Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Forbidden);
        order.Status.Should().Be(OrderStatus.Pending);
    }

    [Fact]
    public async Task ConfirmOrderAsync_WhenOrderDoesNotExist_ReturnsNotFound()
    {
        var fixture = new Fixture();

        var result = await fixture.CreateOrderService().ConfirmOrderAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
        fixture.PaymentGateway.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task CancelOrderAsync_WhenOrderDoesNotExist_ReturnsNotFound()
    {
        var fixture = new Fixture();

        var result = await fixture.CreateOrderService().CancelOrderAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
    }

    [Fact]
    public async Task CancelOrderAsync_WhenOrderReferencesASeatThatNoLongerExists_ReturnsNotFound()
    {
        var (fixture, order, buyerId) = await PlaceOrderAsync(new Fixture());
        // 模擬「order.Items 引用的座位查不到」這個理論上不該發生的內部資料不一致情境
        // （見 ticketing-purchase design.md 決策 2 第 4 點）：直接把 Fake 座位資料清空。
        fixture.EventSeatRepository.Data.Clear();

        var result = await fixture.CreateOrderService().CancelOrderAsync(order.Id, buyerId, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
        order.Status.Should().Be(OrderStatus.Pending);
    }

    // ---- CancelExpiredOrderAsync ----

    [Fact]
    public async Task CancelExpiredOrderAsync_WhenOrderIsExpired_SucceedsWithoutAnyBuyerIdentity()
    {
        var (fixture, order, _) = await PlaceOrderAsync(new Fixture());
        fixture.DateTimeProvider.UtcNow = order.HeldUntilUtc.AddSeconds(1);

        var result = await fixture.CreateOrderService().CancelExpiredOrderAsync(order.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Cancelled);
    }

    [Fact]
    public async Task CancelExpiredOrderAsync_WhenOrderIsNotYetExpired_ReturnsConflictAndDoesNotChangeOrder()
    {
        var (fixture, order, _) = await PlaceOrderAsync(new Fixture());
        fixture.DateTimeProvider.UtcNow = order.HeldUntilUtc.AddSeconds(-1);

        var result = await fixture.CreateOrderService().CancelExpiredOrderAsync(order.Id, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Conflict);
        order.Status.Should().Be(OrderStatus.Pending);
    }

    [Fact]
    public async Task CancelExpiredOrderAsync_WhenNowEqualsHeldUntilUtc_TreatsAsExpiredAndSucceeds()
    {
        // 邊界案例：跟 Order.GetStatus 的 now >= HeldUntilUtc 判斷邊界一致
        // （見 ticketing-order-management tasks.md 4.1）。
        var (fixture, order, _) = await PlaceOrderAsync(new Fixture());
        fixture.DateTimeProvider.UtcNow = order.HeldUntilUtc;

        var result = await fixture.CreateOrderService().CancelExpiredOrderAsync(order.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Cancelled);
    }

    [Fact]
    public async Task CancelExpiredOrderAsync_WhenOrderDoesNotExist_ReturnsNotFound()
    {
        var fixture = new Fixture();

        var result = await fixture.CreateOrderService().CancelExpiredOrderAsync(Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
    }

    // ---- 純計數（不綁座位）訂單的確認/取消/逾時清理 ----

    private async Task<(Fixture Fixture, Order Order, Guid BuyerId, TicketType TicketType)> PlaceCountingOrderAsync(
        Fixture fixture, int availableQuantity = 10, int quantity = 3)
    {
        var (_, ticketType) = fixture.SeedEventWithCountBasedTicketType(availableQuantity);
        var buyerId = Guid.NewGuid();
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(null, ticketType.Id, quantity)]);
        var result = await fixture.CreateOrderService().PlaceOrderAsync(buyerId, request, CancellationToken.None);
        return (fixture, fixture.OrderRepository.Data.Single(o => o.Id == result.Value), buyerId, ticketType);
    }

    [Fact]
    public async Task ConfirmOrderAsync_WhenPureCountingOrder_SucceedsWithoutFurtherReducingAvailableQuantity()
    {
        var (fixture, order, buyerId, ticketType) = await PlaceCountingOrderAsync(new Fixture(), availableQuantity: 10, quantity: 3);

        var result = await fixture.CreateOrderService().ConfirmOrderAsync(order.Id, buyerId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Paid);
        ticketType.AvailableQuantity.Should().Be(7);
    }

    [Fact]
    public async Task ConfirmOrderAsync_WhenPureCountingOrderWithQuantityGreaterThanOne_ChargesUnitPriceTimesQuantity()
    {
        var (fixture, order, buyerId, ticketType) = await PlaceCountingOrderAsync(new Fixture(), availableQuantity: 10, quantity: 3);

        await fixture.CreateOrderService().ConfirmOrderAsync(order.Id, buyerId, CancellationToken.None);

        fixture.PaymentGateway.LastAmount.Should().Be(ticketType.Price * 3);
    }

    [Fact]
    public async Task CancelOrderAsync_WhenPureCountingOrder_RestoresAvailableQuantity()
    {
        var (fixture, order, buyerId, ticketType) = await PlaceCountingOrderAsync(new Fixture(), availableQuantity: 10, quantity: 3);

        var result = await fixture.CreateOrderService().CancelOrderAsync(order.Id, buyerId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Cancelled);
        ticketType.AvailableQuantity.Should().Be(10);
    }

    [Fact]
    public async Task CancelExpiredOrderAsync_WhenPureCountingOrderIsExpired_RestoresAvailableQuantity()
    {
        var (fixture, order, _, ticketType) = await PlaceCountingOrderAsync(new Fixture(), availableQuantity: 10, quantity: 3);
        fixture.DateTimeProvider.UtcNow = order.HeldUntilUtc.AddSeconds(1);

        var result = await fixture.CreateOrderService().CancelExpiredOrderAsync(order.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Cancelled);
        ticketType.AvailableQuantity.Should().Be(10);
    }

    // ---- 混合訂單（座位 + 計數同時存在）的確認/取消 ----
    // 外部審查抓到：task 7.4 原本標記完成，但只補了純計數訂單與混合訂單「建立」的測試，
    // 缺這一段混合訂單「確認/取消」的測試——邏輯上分流看起來正確，但沒有測試佐證不該算完成。

    private async Task<(Fixture Fixture, Order Order, Guid BuyerId, EventSeat EventSeat, TicketType CountTicketType)> PlaceMixedOrderAsync(
        Fixture fixture, int availableQuantity = 10, int quantity = 2)
    {
        var (_, _, eventSeat, seatTicketType) = fixture.SeedEventWithSeatAndTicketType();
        var @event = fixture.EventRepository.Data.Single(e => e.Id == eventSeat.EventId);
        var countTicketType = @event.CreateCountBasedTicketType("站票", 300m, availableQuantity);
        fixture.TicketTypeRepository.Data.Add(countTicketType);

        var buyerId = Guid.NewGuid();
        var request = new PlaceOrderRequest([
            new PlaceOrderSelectionRequest(eventSeat.Id, seatTicketType.Id),
            new PlaceOrderSelectionRequest(null, countTicketType.Id, quantity)
        ]);
        var result = await fixture.CreateOrderService().PlaceOrderAsync(buyerId, request, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();

        return (fixture, fixture.OrderRepository.Data.Single(o => o.Id == result.Value), buyerId, eventSeat, countTicketType);
    }

    [Fact]
    public async Task ConfirmOrderAsync_WhenMixedOrder_MarksSeatSoldAndDoesNotFurtherReduceCountingQuantity()
    {
        var (fixture, order, buyerId, eventSeat, countTicketType) = await PlaceMixedOrderAsync(new Fixture(), availableQuantity: 10, quantity: 2);
        var quantityAfterPlace = countTicketType.AvailableQuantity;

        var result = await fixture.CreateOrderService().ConfirmOrderAsync(order.Id, buyerId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Paid);
        eventSeat.GetStatus(fixture.DateTimeProvider.UtcNow).Should().Be(EventSeatStatus.Sold);
        countTicketType.AvailableQuantity.Should().Be(quantityAfterPlace, "確認訂單不應該再次扣減計數項目的庫存");
    }

    [Fact]
    public async Task ConfirmOrderAsync_WhenMixedOrder_ChargesSumOfSeatUnitPriceAndCountingUnitPriceTimesQuantity()
    {
        var (fixture, order, buyerId, _, countTicketType) = await PlaceMixedOrderAsync(new Fixture(), availableQuantity: 10, quantity: 2);
        var seatUnitPrice = order.Items.Single(i => i.EventSeatId != null).UnitPrice;

        await fixture.CreateOrderService().ConfirmOrderAsync(order.Id, buyerId, CancellationToken.None);

        fixture.PaymentGateway.LastAmount.Should().Be(seatUnitPrice + countTicketType.Price * 2);
    }

    [Fact]
    public async Task CancelOrderAsync_WhenMixedOrder_ReleasesSeatAndRestoresCountingQuantity()
    {
        var (fixture, order, buyerId, eventSeat, countTicketType) = await PlaceMixedOrderAsync(new Fixture(), availableQuantity: 10, quantity: 2);

        var result = await fixture.CreateOrderService().CancelOrderAsync(order.Id, buyerId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Cancelled);
        eventSeat.GetStatus(fixture.DateTimeProvider.UtcNow).Should().Be(EventSeatStatus.Available);
        countTicketType.AvailableQuantity.Should().Be(10, "取消混合訂單須完整歸還計數項目的庫存，不能只釋放座位");
    }
}
