using FluentAssertions;
using Microsoft.Extensions.Logging;
using ProjectC.Application.Common;
using ProjectC.Application.Orders;
using ProjectC.Application.Orders.PlaceOrder;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Application.Tickets.GetTicketTypes;
using ProjectC.Domain.Events;
using ProjectC.Domain.Members;
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
        public FakeApplicationDbContext DbContext { get; } = new();
        public FakeEmailNotificationService EmailNotificationService { get; } = new();
        public FakeQueryCache QueryCache { get; } = new();
        public FakePurchaseQueueAdmissionMirror AdmissionMirror { get; } = new();
        public FakeMemberRealNameRepository MemberRealNameRepository { get; } = new();
        public CapturingLogger<OrderService> Logger { get; } = new();

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
            new CancelOrderHandler(DateTimeProvider),
            EmailNotificationService,
            DbContext,
            Logger,
            QueryCache,
            AdmissionMirror,
            MemberRealNameRepository);

        public (Event Event, SeatMap SeatMap, EventSeat EventSeat, TicketType TicketType) SeedEventWithSeatAndTicketType(
            string seatZoneCode = "A", string ticketTypeZoneCode = "A", bool isRealNameRequired = false,
            DateTime? startAtUtc = null, DateTime? salesStartAtUtc = null, DateTime? salesEndAtUtc = null)
        {
            var seatMap = new SeatMap(Guid.NewGuid(), Guid.NewGuid());
            var seat = seatMap.AddSeat(seatZoneCode, "1");
            var @event = new Event(Guid.NewGuid(), "Concert", startAtUtc ?? Now.AddDays(1), Guid.NewGuid(), seatMap.Id, Guid.NewGuid(), isRealNameRequired: isRealNameRequired,
                salesStartAtUtc: salesStartAtUtc, salesEndAtUtc: salesEndAtUtc);
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
            int seatCount, int? maxTicketsPerOrder, string zoneCode = "A", bool isRealNameRequired = false, DateTime? salesStartAtUtc = null)
        {
            var seatMap = new SeatMap(Guid.NewGuid(), Guid.NewGuid());
            var seatTemplates = Enumerable.Range(1, seatCount).Select(n => seatMap.AddSeat(zoneCode, n.ToString())).ToList();
            var @event = new Event(
                Guid.NewGuid(), "Concert", Now.AddDays(1), Guid.NewGuid(), seatMap.Id, Guid.NewGuid(), maxTicketsPerOrder: maxTicketsPerOrder, isRealNameRequired: isRealNameRequired,
                salesStartAtUtc: salesStartAtUtc);
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
                Guid.NewGuid(), "Concert", Now.AddDays(1), Guid.NewGuid(), seatMap.Id, Guid.NewGuid(), maxTicketsPerOrder: maxTicketsPerOrder);
            var ticketType = @event.CreateCountBasedTicketType("站票", price, availableQuantity);

            EventRepository.Data.Add(@event);
            SeatMapRepository.Data.Add(seatMap);
            TicketTypeRepository.Data.Add(ticketType);

            return (@event, ticketType);
        }

        /// <summary>
        /// 座位樣板屬於另一張座位圖的異常資料（order-placement-p95-optimization tasks.md 4.1、TP-ORDER-030）：
        /// 活動 E 用座位圖 M1，EventSeat 的 SeatId 卻指向 M2 的座位。EventSeat 建構子是 internal，
        /// 由一個只在這裡使用、不加入任何 repository 的同 Id 活動（SeatMapId = M2）產生。
        /// </summary>
        public (Event Event, EventSeat ForeignEventSeat, TicketType TicketType) SeedEventWithSeatFromAnotherSeatMap(
            string foreignSeatZoneCode, string ticketTypeZoneCode)
        {
            var eventSeatMap = new SeatMap(Guid.NewGuid(), Guid.NewGuid());
            eventSeatMap.AddSeat(ticketTypeZoneCode, "1");
            var otherSeatMap = new SeatMap(Guid.NewGuid(), Guid.NewGuid());
            otherSeatMap.AddSeat(foreignSeatZoneCode, "1");

            var @event = new Event(Guid.NewGuid(), "Concert", Now.AddDays(1), Guid.NewGuid(), eventSeatMap.Id, Guid.NewGuid());
            var ticketType = @event.CreateTicketType(ticketTypeZoneCode, 500m, eventSeatMap);
            var testOnlyTwin = new Event(@event.Id, "Concert", Now.AddDays(1), Guid.NewGuid(), otherSeatMap.Id, Guid.NewGuid());
            var foreignEventSeat = testOnlyTwin.CreateEventSeats(otherSeatMap).Single();

            EventRepository.Data.Add(@event);
            SeatMapRepository.Data.Add(eventSeatMap);
            SeatMapRepository.Data.Add(otherSeatMap);
            EventSeatRepository.Data.Add(foreignEventSeat);
            TicketTypeRepository.Data.Add(ticketType);

            return (@event, foreignEventSeat, ticketType);
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

    // ---- 分區／座位圖成員比對移到交易前（order-placement-p95-optimization 優化 C） ----
    // 目的：注定被拒的座位請求不進 Event 列鎖佇列、不占連線；鎖內不再載入整張座位圖（座位票 InLock 的主要成本）。

    [Fact]
    public async Task PlaceOrderAsync_WhenSeatZoneDoesNotMatchTicketTypeZone_RejectsBeforeOpeningTransaction()
    {
        // TP-ORDER-021
        var fixture = new Fixture();
        var (_, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType(seatZoneCode: "A", ticketTypeZoneCode: "B");
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeat.Id, ticketType.Id)]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.Validation);
        result.Error.Message.Should().Be(
            $"Seat '{eventSeat.Id}' belongs to zone 'A', which does not match ticket type zone 'B'.", "訊息與搬移前的鎖內檢查相同");
        fixture.UnitOfWork.BeginTransactionCallCount.Should().Be(0, "分區不一致在交易前就能判斷，不得排進 Event 列鎖");
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenQueueModeEnabledAndNotAdmittedAndSeatZoneMismatches_ReturnsValidationErrorNotQueueAdmissionRequired()
    {
        // TP-ORDER-026：分區檢查移到交易前後，未入場買家選錯分區先得到 400，而非鎖內的 403。
        var fixture = new Fixture();
        var (@event, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType(seatZoneCode: "A", ticketTypeZoneCode: "B");
        @event.EnableQueueMode();
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeat.Id, ticketType.Id)]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.Validation);
        fixture.UnitOfWork.BeginTransactionCallCount.Should().Be(0);
        fixture.PurchaseQueueRepository.GetForUpdateCallCount.Should().Be(0);
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenSeatTemplateDoesNotExist_ReturnsNotFoundBeforeOpeningTransaction()
    {
        // TP-ORDER-029
        var fixture = new Fixture();
        var (_, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType();
        fixture.SeatMapRepository.Data.Clear();
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeat.Id, ticketType.Id)]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.NotFound);
        fixture.OrderRepository.Data.Should().BeEmpty();
        fixture.UnitOfWork.BeginTransactionCallCount.Should().Be(0);
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenSeatTemplateBelongsToAnotherSeatMap_ReturnsNotFoundBeforeOpeningTransaction()
    {
        // TP-ORDER-030（交易外讀到活動）：票種分區刻意與座位不同，回 404 而非 400 證明成員比對先於分區比對。
        var fixture = new Fixture();
        var (_, foreignEventSeat, ticketType) = fixture.SeedEventWithSeatFromAnotherSeatMap(foreignSeatZoneCode: "B", ticketTypeZoneCode: "A");
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(foreignEventSeat.Id, ticketType.Id)]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.NotFound);
        result.Error.Message.Should().Be($"Seat '{foreignEventSeat.SeatId}' was not found in the seat map.");
        fixture.OrderRepository.Data.Should().BeEmpty();
        fixture.UnitOfWork.BeginTransactionCallCount.Should().Be(0);
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenEventUnreadOutsideTransactionAndSeatTemplateBelongsToAnotherSeatMap_ReturnsNotFoundBeforeQueueCheckAndLocks()
    {
        // TP-ORDER-030（交易外讀不到活動）：成員比對移到鎖內，順序為成員 → 分區 → 排隊資格 → 座位／票種鎖定。
        // 排隊模式且未入場、分區也不一致，回 404 才證明成員比對同時先於分區與排隊資格。
        var fixture = new Fixture();
        var (@event, foreignEventSeat, ticketType) = fixture.SeedEventWithSeatFromAnotherSeatMap(foreignSeatZoneCode: "B", ticketTypeZoneCode: "A");
        @event.EnableQueueMode();
        fixture.EventRepository.GetByIdOverride = _ => null;
        fixture.EventRepository.GetForShareOverride = _ => @event;
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(foreignEventSeat.Id, ticketType.Id)]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.NotFound);
        fixture.OrderRepository.Data.Should().BeEmpty();
        fixture.UnitOfWork.BeginTransactionCallCount.Should().Be(1);
        fixture.PurchaseQueueRepository.GetForUpdateCallCount.Should().Be(0);
        fixture.EventSeatRepository.GetForUpdateCallCount.Should().Be(0);
        fixture.TicketTypeRepository.GetForUpdateCallCount.Should().Be(0);
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenEventUnreadOutsideTransactionAndSeatZoneMismatches_ReturnsValidationErrorBeforeQueueCheckAndLocks()
    {
        // TP-ORDER-033：交易外讀不到活動就無法比對座位圖成員，分區比對隨之移到鎖內、排隊資格與任何鎖定之前。
        var fixture = new Fixture();
        var (@event, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType(seatZoneCode: "A", ticketTypeZoneCode: "B");
        @event.EnableQueueMode();
        fixture.EventRepository.GetByIdOverride = _ => null;
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeat.Id, ticketType.Id)]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.Validation);
        fixture.UnitOfWork.BeginTransactionCallCount.Should().Be(1);
        fixture.PurchaseQueueRepository.GetForUpdateCallCount.Should().Be(0);
        fixture.EventSeatRepository.GetForUpdateCallCount.Should().Be(0);
        fixture.TicketTypeRepository.GetForUpdateCallCount.Should().Be(0);
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenMaxTicketsPerOrderAndSeatZoneBothViolated_ReturnsMaxTicketsErrorBeforeOpeningTransaction()
    {
        // TP-ORDER-032：搬移分區檢查不得改變既有的錯誤優先順序（限購先於分區）。
        var fixture = new Fixture();
        var seatMap = new SeatMap(Guid.NewGuid(), Guid.NewGuid());
        seatMap.AddSeat("A", "1");
        seatMap.AddSeat("A", "2");
        seatMap.AddSeat("B", "1");
        var @event = new Event(Guid.NewGuid(), "Concert", Now.AddDays(1), Guid.NewGuid(), seatMap.Id, Guid.NewGuid(), maxTicketsPerOrder: 1);
        var zoneASeats = @event.CreateEventSeats(seatMap).Where(es => seatMap.Seats.Single(s => s.Id == es.SeatId).ZoneCode == "A").ToList();
        var zoneBTicketType = @event.CreateTicketType("B", 500m, seatMap);
        fixture.EventRepository.Data.Add(@event);
        fixture.SeatMapRepository.Data.Add(seatMap);
        fixture.EventSeatRepository.Data.AddRange(zoneASeats);
        fixture.TicketTypeRepository.Data.Add(zoneBTicketType);
        var request = new PlaceOrderRequest(zoneASeats.Select(es => new PlaceOrderSelectionRequest(es.Id, zoneBTicketType.Id)).ToList());

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.Validation);
        result.Error.Message.Should().Be("This event allows at most 1 ticket(s) per order.");
        fixture.UnitOfWork.BeginTransactionCallCount.Should().Be(0);
    }

    [Fact]
    public async Task PlaceOrderAsync_WithSeatSelection_ReadsOnlySelectedSeatTemplatesInsteadOfWholeSeatMap()
    {
        // 優化 C 的效能目的：鎖內不再重讀 Event、不再載入整張座位圖，只在交易前以 SeatId 批次讀樣板。
        var fixture = new Fixture();
        var (_, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType();
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeat.Id, ticketType.Id)]);
        using var cancellationTokenSource = new CancellationTokenSource();

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, cancellationTokenSource.Token);

        result.IsSuccess.Should().BeTrue();
        fixture.SeatMapRepository.GetByIdCallCount.Should().Be(0);
        fixture.SeatMapRepository.GetSeatsByIdsCallCount.Should().Be(1);
        fixture.SeatMapRepository.LastGetSeatsByIdsIds.Should().Equal(eventSeat.SeatId);
        fixture.SeatMapRepository.LastGetSeatsByIdsToken.Should().Be(cancellationTokenSource.Token, "新增的樣板查詢必須沿用呼叫端的取消權杖（hardener 第 4 節）");
        fixture.EventRepository.GetByIdCallCount.Should().Be(1, "活動只在交易外讀一次，鎖內以 lockedEvent 為準");
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
        var eventWithLimit = new Event(@event.Id, @event.Title, @event.StartAtUtc, @event.VenueId, @event.SeatMapId, @event.OrganizerId, maxTicketsPerOrder: 3);
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

    // ---- query-caching：票種列表快取失效觸發點（tasks.md 5.2／5.3／5.5a／5.8／5.9） ----

    [Fact]
    public async Task PlaceOrderAsync_WithCountingSelection_InvalidatesTicketTypesCacheForThatEvent()
    {
        var fixture = new Fixture();
        var (@event, ticketType) = fixture.SeedEventWithCountBasedTicketType(availableQuantity: 10);
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(null, ticketType.Id, 2)]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        fixture.QueryCache.RemoveCalls.Should().ContainSingle(key => key == GetTicketTypesHandler.BuildCacheKey(@event.Id));
    }

    [Fact]
    public async Task PlaceOrderAsync_WithSeatOnlySelection_DoesNotInvalidateTicketTypesCache()
    {
        var fixture = new Fixture();
        var (_, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType();
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeat.Id, ticketType.Id)]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue("純座位制訂單不呼叫 TicketType.Reserve，不應觸發票種列表快取失效");
        fixture.QueryCache.RemoveCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenSecondCountingSelectionFailsAndCompensatingRollbackOccurs_DoesNotInvalidateCacheOrCommit()
    {
        var fixture = new Fixture();
        var (@event, ticketTypeA) = fixture.SeedEventWithCountBasedTicketType(availableQuantity: 10);
        var ticketTypeB = @event.CreateCountBasedTicketType("B區", 300m, 1);
        fixture.TicketTypeRepository.Data.Add(ticketTypeB);
        var request = new PlaceOrderRequest([
            new PlaceOrderSelectionRequest(null, ticketTypeA.Id, 2),
            new PlaceOrderSelectionRequest(null, ticketTypeB.Id, 5), // 超過可售量，觸發 Reserve 失敗、補償回滾
        ]);
        // 交易外讀不到活動時跳過提早 409（order-placement-p95-optimization 決策 5），才走得到鎖內的補償回滾。
        fixture.EventRepository.GetByIdOverride = _ => null;

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        ticketTypeA.AvailableQuantity.Should().Be(10, "第一個項目扣減成功後，第二個項目失敗須把它補回去");
        fixture.UnitOfWork.LastTransaction!.Committed.Should().BeFalse();
        fixture.QueryCache.RemoveCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task ConfirmOrderAsync_WhenPureCountingOrder_DoesNotInvalidateTicketTypesCache()
    {
        var (fixture, order, buyerId, _) = await PlaceCountingOrderAsync(new Fixture(), availableQuantity: 10, quantity: 3);
        fixture.QueryCache.RemoveCalls.Clear();

        var result = await fixture.CreateOrderService().ConfirmOrderAsync(order.Id, buyerId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        fixture.QueryCache.RemoveCalls.Should().BeEmpty("確認付款不變更 AvailableQuantity，不應觸發票種列表快取失效");
    }

    [Fact]
    public async Task CancelOrderAsync_WhenPureCountingOrder_InvalidatesTicketTypesCacheForThatEvent()
    {
        var (fixture, order, buyerId, ticketType) = await PlaceCountingOrderAsync(new Fixture(), availableQuantity: 10, quantity: 3);
        fixture.QueryCache.RemoveCalls.Clear();

        var result = await fixture.CreateOrderService().CancelOrderAsync(order.Id, buyerId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        fixture.QueryCache.RemoveCalls.Should().ContainSingle(key => key == GetTicketTypesHandler.BuildCacheKey(ticketType.EventId));
    }

    [Fact]
    public async Task CancelExpiredOrderAsync_WhenPureCountingOrderIsExpired_InvalidatesTicketTypesCacheForThatEvent()
    {
        var (fixture, order, _, ticketType) = await PlaceCountingOrderAsync(new Fixture(), availableQuantity: 10, quantity: 3);
        fixture.QueryCache.RemoveCalls.Clear();
        fixture.DateTimeProvider.UtcNow = order.HeldUntilUtc.AddSeconds(1);

        var result = await fixture.CreateOrderService().CancelExpiredOrderAsync(order.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        fixture.QueryCache.RemoveCalls.Should().ContainSingle(key => key == GetTicketTypesHandler.BuildCacheKey(ticketType.EventId));
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

    // ---- PlaceOrderAsync：實名閘門（real-name-verification TP-RN-ORDER-*）----

    private static Event CopyWithRealNameRequired(Event source, bool isRealNameRequired) => new(
        source.Id, source.Title, source.StartAtUtc, source.VenueId, source.SeatMapId, source.OrganizerId,
        isRealNameRequired: isRealNameRequired);

    // 閘門的價值在於「被擋下的請求不得佔用任何座位或庫存」，所以失敗案例一律檢查沒有鎖定、沒有扣減、沒有提交。
    private static void AssertNoSeatOrStockTouched(Fixture fixture, EventSeat eventSeat, TicketType ticketType, int? expectedAvailableQuantity)
    {
        fixture.EventSeatRepository.GetForUpdateCallCount.Should().Be(0);
        fixture.TicketTypeRepository.GetForUpdateCallCount.Should().Be(0);
        eventSeat.GetStatus(Now).Should().Be(EventSeatStatus.Available);
        ticketType.AvailableQuantity.Should().Be(expectedAvailableQuantity);
        fixture.OrderRepository.Data.Should().BeEmpty();
        (fixture.UnitOfWork.LastTransaction?.Committed ?? false).Should().BeFalse();
    }

    // TP-RN-ORDER-001
    [Fact]
    public async Task PlaceOrderAsync_WhenRealNameRequiredAndBuyerNotRegistered_ReturnsRealNameRequiredBeforeTransaction()
    {
        var fixture = new Fixture();
        var (@event, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType(isRealNameRequired: true);
        var availableQuantityBefore = ticketType.AvailableQuantity;
        var buyerId = Guid.NewGuid();
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeat.Id, ticketType.Id)]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(buyerId, request, CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.RealNameRequired);
        result.Error.Message.Should().Contain(@event.Id.ToString());
        fixture.UnitOfWork.BeginTransactionCallCount.Should().Be(0);
        fixture.MemberRealNameRepository.LastGetMemberId.Should().Be(buyerId);
        AssertNoSeatOrStockTouched(fixture, eventSeat, ticketType, availableQuantityBefore);
    }

    // TP-RN-ORDER-002：同時確認實名只查一次（主要檢查已查過，補位檢查不得再查）且 token 有向下傳遞。
    [Fact]
    public async Task PlaceOrderAsync_WhenRealNameRequiredAndBuyerRegistered_CreatesOrder()
    {
        var fixture = new Fixture();
        var (_, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType(isRealNameRequired: true);
        var buyerId = Guid.NewGuid();
        fixture.MemberRealNameRepository.Data[buyerId] = new MemberRealName("王小明", "1234");
        using var cancellationTokenSource = new CancellationTokenSource();
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeat.Id, ticketType.Id)]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(buyerId, request, cancellationTokenSource.Token);

        result.IsSuccess.Should().BeTrue();
        fixture.UnitOfWork.LastTransaction!.Committed.Should().BeTrue();
        fixture.MemberRealNameRepository.GetCallCount.Should().Be(1);
        fixture.MemberRealNameRepository.LastGetToken.Should().Be(cancellationTokenSource.Token);
    }

    // TP-RN-ORDER-003：不需實名的活動不得因此多一次查詢，也不得因未登記被擋。
    [Fact]
    public async Task PlaceOrderAsync_WhenRealNameNotRequiredAndBuyerNotRegistered_SucceedsWithoutQueryingRealName()
    {
        var fixture = new Fixture();
        var (_, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType();
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeat.Id, ticketType.Id)]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        fixture.MemberRealNameRepository.GetCallCount.Should().Be(0);
    }

    // TP-RN-ORDER-004：先引導登記實名，登記後才回報張數錯誤，避免使用者修正張數後又被實名擋下。
    [Fact]
    public async Task PlaceOrderAsync_WhenRealNameMissingAndMaxTicketsExceeded_ReturnsRealNameRequired()
    {
        var fixture = new Fixture();
        var (_, ticketType, eventSeats) = fixture.SeedEventWithMultipleSeats(seatCount: 3, maxTicketsPerOrder: 2, isRealNameRequired: true);
        var request = new PlaceOrderRequest(eventSeats
            .Select(seat => new PlaceOrderSelectionRequest(seat.Id, ticketType.Id))
            .ToList());

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.RealNameRequired);
    }

    // TP-RN-ORDER-006：跨活動時尚無唯一活動可判斷是否需實名，必須先回報跨活動錯誤且不查實名。
    [Fact]
    public async Task PlaceOrderAsync_WhenItemsSpanRealNameEventAndOtherEvent_ReturnsCrossEventValidationWithoutQueryingRealName()
    {
        var fixture = new Fixture();
        var (_, _, eventSeatA, ticketTypeA) = fixture.SeedEventWithSeatAndTicketType(isRealNameRequired: true);
        var (_, countTicketTypeB) = fixture.SeedEventWithCountBasedTicketType(availableQuantity: 10);
        var request = new PlaceOrderRequest([
            new PlaceOrderSelectionRequest(eventSeatA.Id, ticketTypeA.Id),
            new PlaceOrderSelectionRequest(null, countTicketTypeB.Id)]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.Validation);
        result.Error.Message.Should().Contain("same event");
        fixture.MemberRealNameRepository.GetCallCount.Should().Be(0);
        countTicketTypeB.AvailableQuantity.Should().Be(10);
        AssertNoSeatOrStockTouched(fixture, eventSeatA, ticketTypeA, ticketTypeA.AvailableQuantity);
    }

    // TP-RN-ORDER-007：交易外讀不到活動時主要檢查被跳過；補位檢查必須在排隊資格與任何鎖定之前擋下。
    // 活動刻意開啟熱門搶購模式，若補位檢查放錯位置，會先回 QueueAdmissionRequired 而非 RealNameRequired。
    [Fact]
    public async Task PlaceOrderAsync_WhenPreTransactionReadIsNullAndLockedEventRequiresRealName_ReturnsRealNameRequiredBeforeQueueCheck()
    {
        var fixture = new Fixture();
        var (@event, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType(isRealNameRequired: true);
        @event.EnableQueueMode();
        var availableQuantityBefore = ticketType.AvailableQuantity;
        fixture.EventRepository.GetByIdOverride = _ => null;
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeat.Id, ticketType.Id)]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.RealNameRequired);
        fixture.MemberRealNameRepository.GetCallCount.Should().Be(1);
        fixture.PurchaseQueueRepository.GetForUpdateCallCount.Should().Be(0);
        AssertNoSeatOrStockTouched(fixture, eventSeat, ticketType, availableQuantityBefore);
    }

    // TP-RN-ORDER-008：兩次讀取不一致代表 I1 被破壞，不論買家是否已登記都不得靜默採信任一方。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlaceOrderAsync_WhenRealNameFlagFlipsFromFalseToTrueBetweenReads_ThrowsWithoutTouchingSeatOrStock(bool isBuyerRegistered)
    {
        var fixture = new Fixture();
        var (@event, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType();
        var availableQuantityBefore = ticketType.AvailableQuantity;
        var buyerId = Guid.NewGuid();
        if (isBuyerRegistered)
            fixture.MemberRealNameRepository.Data[buyerId] = new MemberRealName("王小明", "1234");
        fixture.EventRepository.GetForShareOverride = _ => CopyWithRealNameRequired(@event, true);
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeat.Id, ticketType.Id)]);

        var act = () => fixture.CreateOrderService().PlaceOrderAsync(buyerId, request, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain(@event.Id.ToString());
        AssertNoSeatOrStockTouched(fixture, eventSeat, ticketType, availableQuantityBefore);
    }

    // TP-RN-ORDER-009
    [Fact]
    public async Task PlaceOrderAsync_WhenRealNameFlagFlipsFromTrueToFalseBetweenReads_ThrowsWithoutTouchingSeatOrStock()
    {
        var fixture = new Fixture();
        var (@event, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType(isRealNameRequired: true);
        var availableQuantityBefore = ticketType.AvailableQuantity;
        var buyerId = Guid.NewGuid();
        fixture.MemberRealNameRepository.Data[buyerId] = new MemberRealName("王小明", "1234");
        fixture.EventRepository.GetForShareOverride = _ => CopyWithRealNameRequired(@event, false);
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeat.Id, ticketType.Id)]);

        var act = () => fixture.CreateOrderService().PlaceOrderAsync(buyerId, request, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain(@event.Id.ToString());
        AssertNoSeatOrStockTouched(fixture, eventSeat, ticketType, availableQuantityBefore);
    }

    // ---- PlaceOrderAsync：販售期間（event-sales-window TP-SALES-ORDER-*）----

    private static PlaceOrderRequest CreateSingleSeatRequest(EventSeat eventSeat, TicketType ticketType)
        => new([new PlaceOrderSelectionRequest(eventSeat.Id, ticketType.Id)]);

    // TP-SALES-ORDER-001
    [Fact]
    public async Task PlaceOrderAsync_WhenSalesNotOpen_ReturnsSalesNotOpenWithoutLockingOrAddingOrder()
    {
        var fixture = new Fixture();
        var (@event, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType(salesStartAtUtc: Now.AddHours(1));

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), CreateSingleSeatRequest(eventSeat, ticketType), CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.SalesNotOpen);
        result.Error.Message.Should().Contain(@event.Id.ToString());
        fixture.UnitOfWork.BeginTransactionCallCount.Should().Be(0);
        AssertNoSeatOrStockTouched(fixture, eventSeat, ticketType, ticketType.AvailableQuantity);
    }

    // TP-SALES-ORDER-002：停售時間是半開區間的右端，等於 now 也必須擋下。
    [Theory]
    [InlineData(-60)]
    [InlineData(0)]
    public async Task PlaceOrderAsync_WhenSalesEndReachedOrPassed_ReturnsSalesClosedWithoutLockingOrAddingOrder(int salesEndOffsetMinutes)
    {
        var fixture = new Fixture();
        var (@event, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType(
            salesStartAtUtc: Now.AddDays(-1), salesEndAtUtc: Now.AddMinutes(salesEndOffsetMinutes));

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), CreateSingleSeatRequest(eventSeat, ticketType), CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.SalesClosed);
        result.Error.Message.Should().Contain(@event.Id.ToString());
        AssertNoSeatOrStockTouched(fixture, eventSeat, ticketType, ticketType.AvailableQuantity);
    }

    // TP-SALES-ORDER-003：開賣時間是閉區間的左端，等於 now 即可購買。
    [Fact]
    public async Task PlaceOrderAsync_WhenNowEqualsSalesStart_CreatesOrder()
    {
        var fixture = new Fixture();
        var (_, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType(salesStartAtUtc: Now, salesEndAtUtc: Now.AddHours(1));

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), CreateSingleSeatRequest(eventSeat, ticketType), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        fixture.OrderRepository.Data.Should().ContainSingle(o => o.Id == result.Value);
        fixture.UnitOfWork.LastTransaction!.Committed.Should().BeTrue();
    }

    // TP-SALES-ORDER-004：未設定停售時間時以活動開始時間為停售點。
    [Fact]
    public async Task PlaceOrderAsync_WhenSalesEndIsNullAndEventStarted_ReturnsSalesClosed()
    {
        var fixture = new Fixture();
        var (_, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType(startAtUtc: Now.AddHours(-1));

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), CreateSingleSeatRequest(eventSeat, ticketType), CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.SalesClosed);
        fixture.OrderRepository.Data.Should().BeEmpty();
    }

    // TP-SALES-ORDER-005：既有活動（兩欄位皆 null）在開始前維持可購買，不能因本變更突然擋下。
    [Fact]
    public async Task PlaceOrderAsync_WhenSalesWindowNotSetAndEventNotStarted_CreatesOrder()
    {
        var fixture = new Fixture();
        var (_, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType(startAtUtc: Now.AddDays(1));

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), CreateSingleSeatRequest(eventSeat, ticketType), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        fixture.OrderRepository.Data.Should().ContainSingle(o => o.Id == result.Value);
    }

    // TP-SALES-ORDER-006：尚未開賣時其他條件都無從修正，必須先告知未開賣，而不是引導登記實名或改張數。
    [Fact]
    public async Task PlaceOrderAsync_WhenSalesNotOpenAndRealNameAndLimitAlsoFail_ReturnsSalesNotOpen()
    {
        var fixture = new Fixture();
        var (_, ticketType, eventSeats) = fixture.SeedEventWithMultipleSeats(
            seatCount: 3, maxTicketsPerOrder: 2, isRealNameRequired: true, salesStartAtUtc: Now.AddHours(1));
        var request = new PlaceOrderRequest(eventSeats.Select(seat => new PlaceOrderSelectionRequest(seat.Id, ticketType.Id)).ToList());

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.SalesNotOpen);
        fixture.MemberRealNameRepository.GetCallCount.Should().Be(0);
    }

    // TP-SALES-ORDER-007：跨活動時沒有唯一活動可判斷販售期間，維持既有跨活動錯誤。
    [Fact]
    public async Task PlaceOrderAsync_WhenItemsSpanEventsAndOneIsNotOpen_ReturnsCrossEventValidation()
    {
        var fixture = new Fixture();
        var (_, _, eventSeatA, ticketTypeA) = fixture.SeedEventWithSeatAndTicketType(salesStartAtUtc: Now.AddHours(1));
        var (_, countTicketTypeB) = fixture.SeedEventWithCountBasedTicketType(availableQuantity: 10);
        var request = new PlaceOrderRequest([
            new PlaceOrderSelectionRequest(eventSeatA.Id, ticketTypeA.Id),
            new PlaceOrderSelectionRequest(null, countTicketTypeB.Id)]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.Validation);
        result.Error.Message.Should().Contain("same event");
    }

    // TP-SALES-ORDER-008：等待活動鎖期間跨過停售時點，交易內必須以鎖定後重新取得的 now 判斷，不得沿用交易外的時間。
    [Fact]
    public async Task PlaceOrderAsync_WhenSalesClosesWhileWaitingForEventLock_ReturnsSalesClosedAndRollsBack()
    {
        var fixture = new Fixture();
        var salesEndAtUtc = Now.AddMinutes(1);
        var (@event, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType(salesEndAtUtc: salesEndAtUtc);
        fixture.EventRepository.GetForShareOverride = _ =>
        {
            fixture.DateTimeProvider.UtcNow = salesEndAtUtc;
            return @event;
        };

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), CreateSingleSeatRequest(eventSeat, ticketType), CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.SalesClosed);
        fixture.UnitOfWork.BeginTransactionCallCount.Should().Be(1, "交易外檢查時仍可售，必須進入交易才會被權威檢查擋下");
        fixture.UnitOfWork.LastTransaction!.Committed.Should().BeFalse();
        AssertNoSeatOrStockTouched(fixture, eventSeat, ticketType, ticketType.AvailableQuantity);
    }

    // TP-SALES-ORDER-009：交易外讀不到活動時快速失敗被跳過，交易內的權威檢查仍須擋下，且早於排隊資格。
    [Fact]
    public async Task PlaceOrderAsync_WhenEventMissingOutsideTransactionButClosedInside_ReturnsSalesClosed()
    {
        var fixture = new Fixture();
        var (@event, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType(startAtUtc: Now.AddHours(1), salesEndAtUtc: Now.AddHours(-1));
        @event.EnableQueueMode();
        fixture.EventRepository.GetByIdOverride = _ => null;

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), CreateSingleSeatRequest(eventSeat, ticketType), CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.SalesClosed);
        fixture.PurchaseQueueRepository.GetForUpdateCallCount.Should().Be(0);
        AssertNoSeatOrStockTouched(fixture, eventSeat, ticketType, ticketType.AvailableQuantity);
    }

    // TP-SALES-ORDER-013：009 用不需實名的活動，交易內「販售期間 → 實名補位」對調時不會失敗；這裡以需實名、未登記的買家釘住順序。
    [Fact]
    public async Task PlaceOrderAsync_WhenEventMissingOutsideTransactionAndClosedInsideWithRealNameMissing_ReturnsSalesClosedWithoutRealNameLookup()
    {
        var fixture = new Fixture();
        var (_, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType(
            isRealNameRequired: true, startAtUtc: Now.AddHours(1), salesEndAtUtc: Now.AddHours(-1));
        fixture.EventRepository.GetByIdOverride = _ => null;

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), CreateSingleSeatRequest(eventSeat, ticketType), CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.SalesClosed);
        fixture.MemberRealNameRepository.GetCallCount.Should().Be(0);
        fixture.OrderRepository.Data.Should().BeEmpty();
    }

    // TP-SALES-ORDER-010：排隊放行不代表可以在停售後購買；販售期間檢查先於排隊資格，排隊紀錄不得被查詢或改動。
    [Fact]
    public async Task PlaceOrderAsync_WhenAdmittedButSalesClosed_ReturnsSalesClosedWithoutTouchingQueueEntry()
    {
        var fixture = new Fixture();
        var (@event, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType(startAtUtc: Now.AddHours(1), salesEndAtUtc: Now.AddHours(-1));
        @event.EnableQueueMode();
        var buyerId = Guid.NewGuid();
        var queueEntry = new PurchaseQueueEntry(Guid.NewGuid(), @event.Id, buyerId, Now.AddHours(-2));
        queueEntry.Admit(Now.AddMinutes(-1), Now.AddMinutes(9));
        fixture.PurchaseQueueRepository.Data.Add(queueEntry);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(buyerId, CreateSingleSeatRequest(eventSeat, ticketType), CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.SalesClosed);
        fixture.PurchaseQueueRepository.GetForUpdateCallCount.Should().Be(0);
        queueEntry.Status.Should().Be(PurchaseQueueEntryStatus.Admitted);
        fixture.OrderRepository.Data.Should().BeEmpty();
    }

    // 停售前成立、停售後才付款／取消的 Pending 訂單：座位保留期內仍須能完成，販售期間只限制「新建立」訂單。
    private async Task<(Fixture Fixture, Order Order, Guid BuyerId)> PlacePendingOrderThenCloseSalesAsync()
    {
        var fixture = new Fixture();
        var salesEndAtUtc = Now.AddMinutes(1);
        var (_, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType(salesEndAtUtc: salesEndAtUtc);
        var buyerId = Guid.NewGuid();
        var result = await fixture.CreateOrderService().PlaceOrderAsync(buyerId, CreateSingleSeatRequest(eventSeat, ticketType), CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        fixture.DateTimeProvider.UtcNow = salesEndAtUtc.AddMinutes(1);
        return (fixture, fixture.OrderRepository.Data.Single(o => o.Id == result.Value), buyerId);
    }

    // TP-SALES-ORDER-011
    [Fact]
    public async Task ConfirmOrderAsync_WhenEventSalesClosed_ConfirmsOrderAsPaid()
    {
        var (fixture, order, buyerId) = await PlacePendingOrderThenCloseSalesAsync();

        var result = await fixture.CreateOrderService().ConfirmOrderAsync(order.Id, buyerId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Paid);
    }

    // TP-SALES-ORDER-012
    [Fact]
    public async Task CancelOrderAsync_WhenEventSalesClosed_CancelsOrder()
    {
        var (fixture, order, buyerId) = await PlacePendingOrderThenCloseSalesAsync();

        var result = await fixture.CreateOrderService().CancelOrderAsync(order.Id, buyerId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Cancelled);
    }

    // ---- PlaceOrderAsync：分段耗時 Debug log（order-placement-p95-optimization LT-MEASURE-*）----

    // ---- 交易前提早回 409（order-placement-p95-optimization 優化 A） ----
    // 目的：注定 409 的請求（壓測中九成）不進 Event 列鎖佇列、不占連線；鎖內判斷仍是唯一權威，提早判斷只拒絕不放行。

    private static void MarkSeatSold(EventSeat eventSeat)
    {
        var otherOrderId = Guid.NewGuid();
        eventSeat.Hold(otherOrderId, Now.AddMinutes(10), Now);
        eventSeat.ConfirmSold(otherOrderId, Now);
    }

    private static PurchaseQueueEntry AddAdmittedQueueEntry(Fixture fixture, Event @event, Guid buyerId)
    {
        var entry = new PurchaseQueueEntry(Guid.NewGuid(), @event.Id, buyerId, Now.AddMinutes(-10));
        entry.Admit(Now.AddMinutes(-5), Now.AddMinutes(5));
        fixture.PurchaseQueueRepository.Data.Add(entry);
        return entry;
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenSeatAlreadySold_ReturnsConflictBeforeOpeningTransaction()
    {
        // TP-ORDER-017
        var fixture = new Fixture();
        var (_, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType();
        MarkSeatSold(eventSeat);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), CreateSingleSeatRequest(eventSeat, ticketType), CancellationToken.None);

        result.Error.Should().BeEquivalentTo(PlaceOrderConflictErrors.SeatNoLongerAvailable(eventSeat.Id));
        fixture.UnitOfWork.BeginTransactionCallCount.Should().Be(0);
        fixture.OrderRepository.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenSeatHeldByAnotherUnexpiredOrder_ReturnsConflictBeforeOpeningTransaction()
    {
        // TP-ORDER-018
        var fixture = new Fixture();
        var (_, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType();
        eventSeat.Hold(Guid.NewGuid(), Now.AddMinutes(5), Now);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), CreateSingleSeatRequest(eventSeat, ticketType), CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.Conflict);
        fixture.UnitOfWork.BeginTransactionCallCount.Should().Be(0);
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenSeatHoldAlreadyExpired_IsNotRejectedEarlyAndSucceeds()
    {
        // TP-ORDER-019：逾時的暫扣視同可售，提早判斷不得以內部欄位（_heldByOrderId 非 null）誤判。
        var fixture = new Fixture();
        var (_, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType();
        eventSeat.Hold(Guid.NewGuid(), Now.AddMinutes(-1), Now.AddMinutes(-11));

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), CreateSingleSeatRequest(eventSeat, ticketType), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        fixture.UnitOfWork.BeginTransactionCallCount.Should().Be(1);
        fixture.UnitOfWork.LastTransaction!.Committed.Should().BeTrue();
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenCountTicketTypeInventoryInsufficient_ReturnsConflictBeforeOpeningTransaction()
    {
        // TP-ORDER-020
        var fixture = new Fixture();
        var (_, countTicketType) = fixture.SeedEventWithCountBasedTicketType(availableQuantity: 1);
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(null, countTicketType.Id, 2)]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.Error.Should().BeEquivalentTo(PlaceOrderConflictErrors.TicketTypeInventoryInsufficient(countTicketType.Id));
        fixture.UnitOfWork.BeginTransactionCallCount.Should().Be(0);
        countTicketType.AvailableQuantity.Should().Be(1);
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenSeatSoldAndCountInventoryInsufficient_ReturnsSeatConflictLikeInLockHandler()
    {
        // 兩種衝突同時成立時，提早判斷必須與 CreateOrderHandler 一樣先報座位（即使計數項目排在請求前面），
        // 否則同一個請求的 409 訊息會因被拒在哪一層而不同（design.md 決策 5）。
        var fixture = new Fixture();
        var (@event, _, eventSeat, seatTicketType) = fixture.SeedEventWithSeatAndTicketType();
        var countTicketType = @event.CreateCountBasedTicketType("站票", 300m, 1);
        fixture.TicketTypeRepository.Data.Add(countTicketType);
        MarkSeatSold(eventSeat);
        var request = new PlaceOrderRequest([
            new PlaceOrderSelectionRequest(null, countTicketType.Id, 2),
            new PlaceOrderSelectionRequest(eventSeat.Id, seatTicketType.Id)
        ]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.Error.Should().BeEquivalentTo(PlaceOrderConflictErrors.SeatNoLongerAvailable(eventSeat.Id));
        fixture.UnitOfWork.BeginTransactionCallCount.Should().Be(0);
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenQueueModeReadOutsideTransactionAndNotAdmittedAndSeatSold_ReturnsQueueAdmissionRequired()
    {
        // TP-ORDER-022：交易外讀到排隊模式就跳過提早判斷，403 仍以鎖內重讀為準，不被 409 取代。
        var fixture = new Fixture();
        var (@event, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType();
        @event.EnableQueueMode();
        MarkSeatSold(eventSeat);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), CreateSingleSeatRequest(eventSeat, ticketType), CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.QueueAdmissionRequired);
        fixture.UnitOfWork.BeginTransactionCallCount.Should().Be(1);
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenEventUnreadOutsideTransactionAndSeatSold_OpensTransactionAndReturnsConflictFromLock()
    {
        // TP-ORDER-024：讀不到活動就不知道是否排隊模式，提早判斷跳過，交給鎖內。
        var fixture = new Fixture();
        var (_, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType();
        fixture.EventRepository.GetByIdOverride = _ => null;
        MarkSeatSold(eventSeat);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), CreateSingleSeatRequest(eventSeat, ticketType), CancellationToken.None);

        result.Error.Should().BeEquivalentTo(PlaceOrderConflictErrors.SeatNoLongerAvailable(eventSeat.Id));
        fixture.UnitOfWork.BeginTransactionCallCount.Should().Be(1);
        fixture.EventSeatRepository.GetForUpdateCallCount.Should().Be(1);
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenQueueModeOffOutsideButOnUnderLockAndSeatSold_ReturnsConflictBecauseEarlyCheckUsesOutsideFlag()
    {
        // TP-ORDER-025：不是並發測試。驗證的是「提早判斷以交易外讀到的旗標為準」——
        // 交易外未開排隊，即使鎖內已開，已售座位仍在交易前回 409（spec delta 記錄的已知時間差）。
        var fixture = new Fixture();
        var (@event, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType();
        var queueModeEventUnderLock = new Event(@event.Id, @event.Title, @event.StartAtUtc, @event.VenueId, @event.SeatMapId, @event.OrganizerId);
        queueModeEventUnderLock.EnableQueueMode();
        fixture.EventRepository.GetForShareOverride = _ => queueModeEventUnderLock;
        MarkSeatSold(eventSeat);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), CreateSingleSeatRequest(eventSeat, ticketType), CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.Conflict);
        fixture.UnitOfWork.BeginTransactionCallCount.Should().Be(0);
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenOneOfThreeSeatsSold_ReturnsConflictForSoldSeatAndHoldsNothing()
    {
        // TP-ORDER-027
        var fixture = new Fixture();
        var (_, ticketType, eventSeats) = fixture.SeedEventWithMultipleSeats(seatCount: 3, maxTicketsPerOrder: null);
        MarkSeatSold(eventSeats[1]);
        var request = new PlaceOrderRequest(eventSeats.Select(es => new PlaceOrderSelectionRequest(es.Id, ticketType.Id)).ToList());

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.Error.Should().BeEquivalentTo(PlaceOrderConflictErrors.SeatNoLongerAvailable(eventSeats[1].Id));
        fixture.UnitOfWork.BeginTransactionCallCount.Should().Be(0);
        eventSeats[0].GetStatus(Now).Should().Be(EventSeatStatus.Available);
        eventSeats[2].GetStatus(Now).Should().Be(EventSeatStatus.Available);
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenSeatAvailableButCountInventoryInsufficient_ReturnsConflictAndHoldsNothing()
    {
        // TP-ORDER-028
        var fixture = new Fixture();
        var (@event, _, eventSeat, seatTicketType) = fixture.SeedEventWithSeatAndTicketType();
        var countTicketType = @event.CreateCountBasedTicketType("站票", 300m, 1);
        fixture.TicketTypeRepository.Data.Add(countTicketType);
        var request = new PlaceOrderRequest(
        [
            new PlaceOrderSelectionRequest(eventSeat.Id, seatTicketType.Id),
            new PlaceOrderSelectionRequest(null, countTicketType.Id, 2),
        ]);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), request, CancellationToken.None);

        result.Error.Should().BeEquivalentTo(PlaceOrderConflictErrors.TicketTypeInventoryInsufficient(countTicketType.Id));
        fixture.UnitOfWork.BeginTransactionCallCount.Should().Be(0);
        eventSeat.GetStatus(Now).Should().Be(EventSeatStatus.Available);
        countTicketType.AvailableQuantity.Should().Be(1);
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenRejectedEarlyOrUnderLock_ReturnsSameConflictMessage()
    {
        // 決策 5：同一個已售座位，交易前被拒與鎖內（CreateOrderHandler）被拒，呼叫端看到的訊息必須相同。
        var fixture = new Fixture();
        var (@event, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType();
        MarkSeatSold(eventSeat);
        var buyerId = Guid.NewGuid();
        var orderService = fixture.CreateOrderService();

        var earlyResult = await orderService.PlaceOrderAsync(buyerId, CreateSingleSeatRequest(eventSeat, ticketType), CancellationToken.None);
        fixture.UnitOfWork.BeginTransactionCallCount.Should().Be(0, "第一次應在交易前被拒");

        // 排隊模式且已入場：提早判斷跳過，改由鎖內判斷。
        @event.EnableQueueMode();
        AddAdmittedQueueEntry(fixture, @event, buyerId);
        var lockResult = await orderService.PlaceOrderAsync(buyerId, CreateSingleSeatRequest(eventSeat, ticketType), CancellationToken.None);
        fixture.UnitOfWork.BeginTransactionCallCount.Should().Be(1, "第二次應進鎖後才被拒");

        earlyResult.Error!.Type.Should().Be(ErrorType.Conflict);
        lockResult.Error.Should().BeEquivalentTo(earlyResult.Error);
    }

    // LT-MEASURE-002：提早 409 的請求沒進交易，交易內分段必須是 null，量測才分得出「提早被拒」與「鎖內被拒」。
    [Fact]
    public async Task PlaceOrderAsync_WhenDebugEnabledAndRejectedEarlyWithConflict_LogsConflictWithNullInTransactionPhases()
    {
        var fixture = new Fixture();
        fixture.Logger.IsDebugEnabled = true;
        var (_, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType();
        MarkSeatSold(eventSeat);

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), CreateSingleSeatRequest(eventSeat, ticketType), CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.Conflict);
        fixture.UnitOfWork.BeginTransactionCallCount.Should().Be(0);
        var entry = GetSinglePhaseTimingEntry(fixture);
        entry.Properties["Outcome"].Should().Be("Conflict");
        entry.Properties["ConnectionOpenMs"].Should().BeOfType<double>("提早 409 前已查過資料庫，連線已開啟");
        entry.Properties["PreTransactionMs"].Should().BeOfType<double>();
        entry.Properties["TotalMs"].Should().BeOfType<double>();
        foreach (var field in InTransactionPhaseFields)
            entry.Properties[field].Should().BeNull($"提早 409 沒開交易，{field} 必須是 null");
    }

    // LT-MEASURE-002（order-placement-p95-phase2）：開啟連線後、開交易前的拒絕，ConnectionOpenMs 有值、交易內分段為 null。
    [Fact]
    public async Task PlaceOrderAsync_WhenDebugEnabledAndTicketTypeNotFound_LogsConnectionOpenWithNullInTransactionPhases()
    {
        var fixture = new Fixture();
        fixture.Logger.IsDebugEnabled = true;

        var result = await fixture.CreateOrderService().PlaceOrderAsync(
            Guid.NewGuid(), new PlaceOrderRequest([new PlaceOrderSelectionRequest(null, Guid.NewGuid())]), CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.NotFound);
        AssertRejectedAfterConnectionOpened(GetSinglePhaseTimingEntry(fixture), "NotFound");
    }

    [Fact]
    public async Task PlaceOrderAsync_WhenDebugEnabledAndItemsSpanEvents_LogsConnectionOpenWithNullInTransactionPhases()
    {
        var fixture = new Fixture();
        fixture.Logger.IsDebugEnabled = true;
        var (_, _, eventSeatFromEventA, _) = fixture.SeedEventWithSeatAndTicketType();
        var (_, _, _, ticketTypeFromEventB) = fixture.SeedEventWithSeatAndTicketType();

        var result = await fixture.CreateOrderService().PlaceOrderAsync(
            Guid.NewGuid(), CreateSingleSeatRequest(eventSeatFromEventA, ticketTypeFromEventB), CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.Validation);
        result.Error.Message.Should().Contain("same event", "必須是跨活動檢查擋下，不是 validator");
        AssertRejectedAfterConnectionOpened(GetSinglePhaseTimingEntry(fixture), "Validation");
    }

    private static void AssertRejectedAfterConnectionOpened(CapturedLogEntry entry, string expectedOutcome)
    {
        entry.Properties["Outcome"].Should().Be(expectedOutcome);
        entry.Properties["ConnectionOpenMs"].Should().BeOfType<double>();
        entry.Properties["PreTransactionMs"].Should().BeOfType<double>();
        entry.Properties["TotalMs"].Should().BeOfType<double>();
        foreach (var field in InTransactionPhaseFields)
            entry.Properties[field].Should().BeNull($"交易沒開始，{field} 必須是 null 而不是 0");
    }

    private static readonly string[] PhaseTimingFields =
        ["Outcome", "HasSeatItems", "ConnectionOpenMs", "PreTransactionMs", "BeginTransactionMs", "EventLockWaitMs", "InLockMs", "CommitMs", "TotalMs"];

    private static readonly string[] InTransactionPhaseFields = ["BeginTransactionMs", "EventLockWaitMs", "InLockMs", "CommitMs"];

    private static CapturedLogEntry GetSinglePhaseTimingEntry(Fixture fixture)
    {
        var entry = fixture.Logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Debug).Subject;
        // 欄位集合固定：例外、提前 return 與成功路徑輸出同一組欄位，Seq 查詢才能用同一個範本統計。
        entry.Properties.Keys.Except(["{OriginalFormat}"]).Should().BeEquivalentTo(PhaseTimingFields);
        return entry;
    }

    // LT-MEASURE-001：量測只需要分布，不得帶買家 Id 回溯個別買家。
    [Fact]
    public async Task PlaceOrderAsync_WhenDebugEnabledAndOrderSucceeds_LogsAllPhasesWithoutBuyerId()
    {
        var fixture = new Fixture();
        fixture.Logger.IsDebugEnabled = true;
        var (_, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType();
        var buyerId = Guid.NewGuid();

        var result = await fixture.CreateOrderService().PlaceOrderAsync(buyerId, CreateSingleSeatRequest(eventSeat, ticketType), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var entry = GetSinglePhaseTimingEntry(fixture);
        entry.Properties["Outcome"].Should().Be("Success");
        entry.Properties["HasSeatItems"].Should().Be(true);
        foreach (var field in PhaseTimingFields.Skip(2))
            entry.Properties[field].Should().BeOfType<double>($"成功路徑每個分段都有到達，{field} 不得為 null");
        entry.Message.Should().NotContain(buyerId.ToString());
        entry.Properties.Values.Should().NotContain(buyerId);
        entry.Exception.Should().BeNull();
    }

    // LT-MEASURE-002 前置驗證：validator 擋下時沒開連線也沒開交易，這些分段是「沒到達」而不是 0。
    [Fact]
    public async Task PlaceOrderAsync_WhenDebugEnabledAndRejectedBeforeTransaction_LogsNullInTransactionPhases()
    {
        var fixture = new Fixture();
        fixture.Logger.IsDebugEnabled = true;

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), new PlaceOrderRequest([]), CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.Validation);
        fixture.UnitOfWork.OpenConnectionCallCount.Should().Be(0);
        fixture.UnitOfWork.BeginTransactionCallCount.Should().Be(0);
        var entry = GetSinglePhaseTimingEntry(fixture);
        entry.Properties["Outcome"].Should().Be("Validation");
        entry.Properties["ConnectionOpenMs"].Should().BeNull("validator 失敗不開連線");
        entry.Properties["PreTransactionMs"].Should().BeOfType<double>();
        entry.Properties["TotalMs"].Should().BeOfType<double>();
        foreach (var field in InTransactionPhaseFields)
            entry.Properties[field].Should().BeNull($"交易沒開始，{field} 必須是 null 而不是 0");
    }

    // LT-MEASURE-003：預設關閉時連 Log 都不呼叫，證明 IsEnabled 守門生效、不組裝參數。
    [Fact]
    public async Task PlaceOrderAsync_WhenDebugDisabled_DoesNotCallLogAtDebug()
    {
        var fixture = new Fixture();
        var (_, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType();

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), CreateSingleSeatRequest(eventSeat, ticketType), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        fixture.Logger.Entries.Should().BeEmpty();
    }

    // LT-MEASURE-004：例外路徑仍要輸出（否則量測筆數對不上），但例外訊息可能含 Id 與 SQL 參數，不得進 log。
    [Fact]
    public async Task PlaceOrderAsync_WhenDebugEnabledAndExceptionThrown_LogsExceptionOutcomeAndRethrows()
    {
        var fixture = new Fixture();
        fixture.Logger.IsDebugEnabled = true;
        var (@event, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType();
        fixture.EventRepository.GetForShareOverride = _ => CopyWithRealNameRequired(@event, true);

        var act = () => fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), CreateSingleSeatRequest(eventSeat, ticketType), CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        var entry = GetSinglePhaseTimingEntry(fixture);
        entry.Properties["Outcome"].Should().Be("Exception");
        entry.Properties["ConnectionOpenMs"].Should().BeOfType<double>();
        entry.Exception.Should().BeNull();
        entry.Message.Should().NotContain(thrown.Message);
        entry.Properties.Values.OfType<string>().Should().NotContain(value => value.Contains(@event.Id.ToString()));
    }

    // 分段以 Stopwatch 單調時鐘量測：WSL2 牆上時鐘會跳動，若任一時間點改讀 IDateTimeProvider，該分段會多出約 1 小時。
    [Fact]
    public async Task PlaceOrderAsync_WhenWallClockJumpsDuringOrder_PhaseTimingsUnaffected()
    {
        var fixture = new Fixture();
        fixture.Logger.IsDebugEnabled = true;
        var (@event, _, eventSeat, ticketType) = fixture.SeedEventWithSeatAndTicketType();
        fixture.EventRepository.GetForShareOverride = _ =>
        {
            fixture.DateTimeProvider.UtcNow = fixture.DateTimeProvider.UtcNow.AddHours(1);
            return @event;
        };

        var result = await fixture.CreateOrderService().PlaceOrderAsync(Guid.NewGuid(), CreateSingleSeatRequest(eventSeat, ticketType), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("活動開始於一天後，推進一小時仍在販售期間");
        var entry = GetSinglePhaseTimingEntry(fixture);
        foreach (var field in PhaseTimingFields.Skip(2))
            entry.Properties[field].Should().BeOfType<double>().Which.Should().BeInRange(0, 10_000, $"{field} 不得受牆上時鐘跳動影響（跳動一小時）");
    }

    // 上一個測試抓不到「所有時間點一致改用 DateTime.UtcNow」；以 IL 檢查不受註解與 using static 影響。
    // 只掃 OrderService 與其巢狀型別（含 async 狀態機與 closure），不追進其他類別；把計時搬到其他類別要靠審查把關。
    // Application 層的業務時間一律走 IDateTimeProvider，所以整個 OrderService 都不該直接讀牆上時鐘。
    [Fact]
    public void OrderService_DoesNotReadWallClockDirectly()
    {
        var calledMethods = IlMethodCallReader.GetCalledMethodsIncludingNestedTypes(typeof(OrderService));

        calledMethods.Should().NotContain(
            m => (m.DeclaringType == typeof(DateTime) || m.DeclaringType == typeof(DateTimeOffset)) && (m.Name == "get_Now" || m.Name == "get_UtcNow" || m.Name == "get_Today"),
            "分段耗時必須用 Stopwatch，業務時間必須用 IDateTimeProvider");
        // TimeProvider.GetTimestamp 是單調時鐘，不在禁止之列；只擋讀牆上時鐘的兩個方法。
        calledMethods.Should().NotContain(
            m => m.DeclaringType != null && typeof(TimeProvider).IsAssignableFrom(m.DeclaringType) && (m.Name == nameof(TimeProvider.GetUtcNow) || m.Name == nameof(TimeProvider.GetLocalNow)),
            "業務時間必須用 IDateTimeProvider，fake 才控制得到");
        calledMethods.Should().Contain(
            m => m.DeclaringType == typeof(System.Diagnostics.Stopwatch) && m.Name == nameof(System.Diagnostics.Stopwatch.GetTimestamp),
            "確認 IL 讀取有涵蓋到分段量測的程式碼，避免因讀不到而空洞通過");
    }
}
