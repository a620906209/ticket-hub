using System.Diagnostics;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Application.Events;
using ProjectC.Application.Orders.PlaceOrder;
using ProjectC.Application.Tickets.GetTicketTypes;
using ProjectC.Domain.Events;
using ProjectC.Domain.Members;
using ProjectC.Domain.Notifications;
using ProjectC.Domain.Orders;
using ProjectC.Domain.PurchaseQueue;
using ProjectC.Domain.Tickets;
using ProjectC.Domain.Venues;

namespace ProjectC.Application.Orders;

public sealed class OrderService
{
    private readonly ITicketTypeRepository _ticketTypeRepository;
    private readonly IEventSeatRepository _eventSeatRepository;
    private readonly IEventRepository _eventRepository;
    private readonly ISeatMapRepository _seatMapRepository;
    private readonly IOrderRepository _orderRepository;
    private readonly IPurchaseQueueRepository _purchaseQueueRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IValidator<PlaceOrderRequest> _validator;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly CreateOrderHandler _createOrderHandler;
    private readonly ConfirmOrderHandler _confirmOrderHandler;
    private readonly CancelOrderHandler _cancelOrderHandler;
    private readonly IEmailNotificationService _emailNotificationService;
    private readonly IApplicationDbContext _dbContext;
    private readonly ILogger<OrderService> _logger;
    private readonly IQueryCache _queryCache;
    private readonly IPurchaseQueueAdmissionMirror _admissionMirror;
    private readonly IMemberRealNameRepository _memberRealNameRepository;

    public OrderService(
        ITicketTypeRepository ticketTypeRepository,
        IEventSeatRepository eventSeatRepository,
        IEventRepository eventRepository,
        ISeatMapRepository seatMapRepository,
        IOrderRepository orderRepository,
        IPurchaseQueueRepository purchaseQueueRepository,
        IUnitOfWork unitOfWork,
        IValidator<PlaceOrderRequest> validator,
        IDateTimeProvider dateTimeProvider,
        CreateOrderHandler createOrderHandler,
        ConfirmOrderHandler confirmOrderHandler,
        CancelOrderHandler cancelOrderHandler,
        IEmailNotificationService emailNotificationService,
        IApplicationDbContext dbContext,
        ILogger<OrderService> logger,
        IQueryCache queryCache,
        IPurchaseQueueAdmissionMirror admissionMirror,
        IMemberRealNameRepository memberRealNameRepository)
    {
        _memberRealNameRepository = memberRealNameRepository;
        _ticketTypeRepository = ticketTypeRepository;
        _eventSeatRepository = eventSeatRepository;
        _eventRepository = eventRepository;
        _seatMapRepository = seatMapRepository;
        _orderRepository = orderRepository;
        _purchaseQueueRepository = purchaseQueueRepository;
        _unitOfWork = unitOfWork;
        _validator = validator;
        _dateTimeProvider = dateTimeProvider;
        _createOrderHandler = createOrderHandler;
        _confirmOrderHandler = confirmOrderHandler;
        _cancelOrderHandler = cancelOrderHandler;
        _emailNotificationService = emailNotificationService;
        _dbContext = dbContext;
        _logger = logger;
        _queryCache = queryCache;
        _admissionMirror = admissionMirror;
    }

    public async Task<Result<Guid>> PlaceOrderAsync(Guid buyerId, PlaceOrderRequest request, CancellationToken cancellationToken)
    {
        var timings = new PlaceOrderPhaseTimings(Stopwatch.GetTimestamp());
        var outcome = "Exception";
        try
        {
            var result = await PlaceOrderCoreAsync(buyerId, request, timings, cancellationToken);
            outcome = result.IsSuccess ? "Success" : result.Error!.Type.ToString();
            return result;
        }
        finally
        {
            // 在 finally 統一輸出，涵蓋所有提前 return 與例外路徑（order-placement-p95-optimization design.md 決策 1）。
            // 例外不附帶進 log：例外訊息可能含 Id 與 SQL 參數；結果只記固定字串 Exception。
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                LogPhaseTimings(timings, outcome, request);
            }
        }
    }

    private void LogPhaseTimings(PlaceOrderPhaseTimings timings, string outcome, PlaceOrderRequest request)
    {
        var endTimestamp = Stopwatch.GetTimestamp();
        var hasSeatItems = request?.Selections?.Any(s => s is not null && s.EventSeatId.HasValue) ?? false;
        // 鎖內被拒絕（壓測九成樣本是鎖內 409）沒有 commit，InLock 以結束時間收尾，否則最主要的樣本量不到鎖內耗時；
        // 此時 InLock 含交易 dispose 的回滾時間。
        _logger.LogDebug(
            "PlaceOrder phase timings: Outcome={Outcome} HasSeatItems={HasSeatItems} ConnectionOpenMs={ConnectionOpenMs} PreTransactionMs={PreTransactionMs} " +
            "BeginTransactionMs={BeginTransactionMs} EventLockWaitMs={EventLockWaitMs} InLockMs={InLockMs} CommitMs={CommitMs} TotalMs={TotalMs}",
            outcome,
            hasSeatItems,
            PlaceOrderPhaseTimings.GetElapsedMilliseconds(timings.Start, timings.ConnectionOpened),
            PlaceOrderPhaseTimings.GetElapsedMilliseconds(timings.Start, timings.TransactionStarting ?? endTimestamp),
            PlaceOrderPhaseTimings.GetElapsedMilliseconds(timings.TransactionStarting, timings.TransactionBegun),
            PlaceOrderPhaseTimings.GetElapsedMilliseconds(timings.TransactionBegun, timings.EventLocked),
            PlaceOrderPhaseTimings.GetElapsedMilliseconds(timings.EventLocked, timings.CommitStarting ?? endTimestamp),
            PlaceOrderPhaseTimings.GetElapsedMilliseconds(timings.CommitStarting, timings.Committed),
            PlaceOrderPhaseTimings.GetElapsedMilliseconds(timings.Start, endTimestamp));
    }

    // 時間點一律以 Stopwatch 單調時鐘取得：WSL2 牆上時鐘會倒退，時間戳相減會失真（design.md 決策 1）。
    // 未到達的時間點為 null，對應分段輸出 null 而非 0，才能區分「沒到達」與「很快」。
    private sealed class PlaceOrderPhaseTimings(long start)
    {
        public long Start { get; } = start;
        public long? ConnectionOpened { get; set; }
        public long? TransactionStarting { get; set; }
        public long? TransactionBegun { get; set; }
        public long? EventLocked { get; set; }
        public long? CommitStarting { get; set; }
        public long? Committed { get; set; }

        public static double? GetElapsedMilliseconds(long? from, long? to)
            => from is { } fromValue && to is { } toValue ? Stopwatch.GetElapsedTime(fromValue, toValue).TotalMilliseconds : null;
    }

    private async Task<Result<Guid>> PlaceOrderCoreAsync(
        Guid buyerId, PlaceOrderRequest request, PlaceOrderPhaseTimings timings, CancellationToken cancellationToken)
    {
        var validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return Result<Guid>.Failure(Error.Validation(string.Join(" ", validation.Errors.Select(e => e.ErrorMessage))));
        }

        // 整筆下單只向連線池借一次連線，避免每次查詢後歸還、下次重新排到隊尾（order-placement-p95-phase2 design.md 決策 2）。
        // 宣告在交易之前，離開方法時先回滾交易再關連線。
        await using var connection = await _unitOfWork.OpenConnectionAsync(cancellationToken);
        timings.ConnectionOpened = Stopwatch.GetTimestamp();

        // no-tracking 查詢，純粹用於存在性／RequiresSeat 交叉驗證，MUST NOT 對這裡取得的實例呼叫
        // TicketType.Reserve()（design.md 決策 3——之後的 GetForUpdateAsync 才是鎖定後的版本）。
        var ticketTypesById = new Dictionary<Guid, TicketType>();
        foreach (var ticketTypeId in request.Selections.Select(s => s.TicketTypeId).Distinct())
        {
            var ticketType = await _ticketTypeRepository.GetByIdAsync(ticketTypeId, cancellationToken);
            if (ticketType is null)
            {
                return Result<Guid>.Failure(Error.NotFound($"Ticket type '{ticketTypeId}' was not found."));
            }

            ticketTypesById[ticketTypeId] = ticketType;
        }

        // RequiresSeat 與請求形狀的交叉驗證：純計數票種指定了座位／綁座位票種未指定座位／
        // 座位項目指定非 1 的 Quantity 皆 MUST 拒絕（design.md 決策 4，ticket-purchase spec 三個 Scenario）。
        foreach (var selectionRequest in request.Selections)
        {
            var ticketType = ticketTypesById[selectionRequest.TicketTypeId];

            if (!ticketType.RequiresSeat && selectionRequest.EventSeatId.HasValue)
            {
                return Result<Guid>.Failure(Error.Validation(
                    $"Ticket type '{ticketType.Id}' does not require a seat but an event seat was specified."));
            }

            if (ticketType.RequiresSeat && !selectionRequest.EventSeatId.HasValue)
            {
                return Result<Guid>.Failure(Error.Validation(
                    $"Ticket type '{ticketType.Id}' requires a seat but none was specified."));
            }

            if (ticketType.RequiresSeat && selectionRequest.Quantity != 1)
            {
                return Result<Guid>.Failure(Error.Validation(
                    $"Seat item for ticket type '{ticketType.Id}' must have a quantity of exactly 1."));
            }
        }

        // 座位存在性／所屬活動比對也 MUST 在取得任何資料庫鎖之前完成，理由跟下面的跨活動檢查相同。
        // no-tracking 批次查詢，不可用 GetForUpdateAsync——那個方法會鎖定，且交易還沒開始也無法呼叫。
        var seatSelectionEventSeatIds = request.Selections
            .Where(s => s.EventSeatId.HasValue)
            .Select(s => s.EventSeatId!.Value)
            .Distinct()
            .ToList();
        var validationSeatsById = new Dictionary<Guid, EventSeat>();
        if (seatSelectionEventSeatIds.Count > 0)
        {
            var validationSeats = await _eventSeatRepository.GetByIdsAsync(seatSelectionEventSeatIds, cancellationToken);
            if (validationSeats.Count < seatSelectionEventSeatIds.Count)
            {
                return Result<Guid>.Failure(Error.NotFound("One or more selected seats were not found."));
            }

            validationSeatsById = validationSeats.ToDictionary(es => es.Id);
        }

        // 跨活動驗證 MUST 在取得任何資料庫鎖之前完成（ticket-ordering spec「建立訂單並原子性鎖定座位
        // 或扣減票種庫存」Requirement：「在嘗試鎖定或扣減任何項目之前，系統 MUST 先驗證...所有項目
        // 須全部屬於同一場活動」）——外部審查抓到兩輪問題：① 先前這裡只用「第一個票種的活動」查
        // 限購張數，真正的跨活動檢查延後到 CreateOrderHandler.Handle 才做；② 改成比對 ticketTypesById
        // 之後，仍只看票種的 EventId，沒把座位所屬活動納入比對集合——只有一個票種時（例如一個綁座位
        // 票種配另一場活動的座位），這個比對會通過，直到座位被鎖定後才被 `ticketType.EventId !=
        // eventSeat.EventId` 擋下。這裡改成「票種活動」跟「座位活動」的聯集：只要聯集大小是 1，數學上
        // 保證每個座位跟其配對票種的活動必然相同（若有任何一組不一致，聯集大小必然 >= 2），
        // 不需要額外逐一比對座位與其配對票種。
        var distinctEventIds = ticketTypesById.Values.Select(t => t.EventId)
            .Concat(validationSeatsById.Values.Select(es => es.EventId))
            .Distinct()
            .ToList();
        if (distinctEventIds.Count > 1)
        {
            return Result<Guid>.Failure(Error.Validation("All selected items must belong to the same event."));
        }

        // 每筆訂單限購張數：依 Quantity 加總（座位項目已在上面驗證固定為 1，語意自然相容）。
        // 上面的跨活動檢查已經保證所有票種屬於同一場活動，這裡不再是「任選第一個」，是唯一的活動。
        var orderEvent = await _eventRepository.GetByIdAsync(distinctEventIds[0], cancellationToken);

        // 販售期間快速失敗：交易外以未加鎖的 orderEvent 判斷，只為了在開賣前／停售後不開交易、不排鎖；
        // 交易內以 lockedEvent 與重新取得的 now 再檢查一次才是權威（event-sales-window design.md 決策 3）。
        if (orderEvent is not null && EventSalesWindowErrors.GetErrorOrNull(orderEvent, _dateTimeProvider.UtcNow) is { } outsideSalesError)
        {
            return Result<Guid>.Failure(outsideSalesError);
        }

        // 實名閘門（主要檢查）放在交易外、限購之前（real-name-verification design.md 決策 3）。交易外判斷之所以安全，
        // 依賴兩個不變量：I1 Event.IsRealNameRequired 建構後不可變；I2 會員實名只能從未登記變成已登記。
        // 未來若新增活動編輯可改此旗標（破壞 I1），必須改以交易內 lockedEvent 為唯一權威，比照 IsQueueModeEnabled；
        // 若新增會員刪除／實名清除（破壞 I2），會員實名的讀取必須移進交易並加鎖，或改在訂單保存實名快照。
        if (orderEvent is { IsRealNameRequired: true } &&
            await _memberRealNameRepository.GetAsync(buyerId, cancellationToken) is null)
        {
            return Result<Guid>.Failure(Error.RealNameRequired($"Event '{orderEvent.Id}' requires real-name registration."));
        }

        if (orderEvent is { MaxTicketsPerOrder: { } maxTicketsPerOrder })
        {
            // Quantity 是外部輸入，validator 只保證 >= 1、沒有上限；Sum(int) 用的是 checked int 累加，
            // 兩個刻意送出接近 int.MaxValue 的 Quantity 就會拋 OverflowException、變成未預期的 500，
            // 而不是乾淨的驗證錯誤——改用 long 累加規避溢位（外部審查抓到）。
            var totalQuantity = request.Selections.Sum(s => (long)s.Quantity);
            if (totalQuantity > maxTicketsPerOrder)
            {
                return Result<Guid>.Failure(Error.Validation($"This event allows at most {maxTicketsPerOrder} ticket(s) per order."));
            }
        }

        // 座位樣板只依 SeatId 批次讀取（no-tracking），取代鎖內載入整張座位圖——那是座位票鎖內時間的主要成本
        // （order-placement-p95-optimization design.md 決策 4）。在交易外以未加鎖的資料判斷之所以安全，依賴
        // Seat.SeatMapId／Seat.ZoneCode／EventSeat.SeatId／EventSeat.EventId／Event.SeatMapId 建構後不可變
        // （ProjectC.Domain.Tests SeatPlacementImmutabilityTests 守住）；若未來任一欄位可變更，這些比對必須移回鎖內。
        var seatPlacements = new List<SeatPlacement>();
        if (validationSeatsById.Count > 0)
        {
            var seatIds = validationSeatsById.Values.Select(es => es.SeatId).Distinct().ToList();
            var seatTemplatesById = (await _seatMapRepository.GetSeatsByIdsAsync(seatIds, cancellationToken)).ToDictionary(s => s.Id);
            if (validationSeatsById.Values.FirstOrDefault(es => !seatTemplatesById.ContainsKey(es.SeatId)) is { } seatWithoutTemplate)
            {
                return Result<Guid>.Failure(Error.NotFound($"Seat '{seatWithoutTemplate.SeatId}' was not found in the seat map."));
            }

            seatPlacements = request.Selections
                .Where(s => s.EventSeatId.HasValue)
                .Select(s =>
                {
                    var eventSeat = validationSeatsById[s.EventSeatId!.Value];
                    return new SeatPlacement(eventSeat, seatTemplatesById[eventSeat.SeatId], ticketTypesById[s.TicketTypeId]);
                })
                .ToList();
        }

        // 交易外讀不到活動時不知道它的 SeatMapId，成員與分區比對改在鎖內以 lockedEvent 補上（見下方實名補位之後）。
        if (orderEvent is not null && GetSeatPlacementErrorOrNull(seatPlacements, orderEvent.SeatMapId) is { } seatPlacementError)
        {
            return Result<Guid>.Failure(seatPlacementError);
        }

        // 提早 409：以交易前的 no-tracking 資料判斷注定失敗的請求，不開交易、不等鎖（design.md 決策 5）。
        // 只拒絕不放行，鎖內 CreateOrderHandler 仍是唯一權威。交易外讀不到活動或讀到排隊模式時跳過：
        // 排隊模式的 403 必須以鎖內重讀為準，不得被這裡的 409 取代。
        if (orderEvent is { IsQueueModeEnabled: false } &&
            GetEarlyConflictOrNull(request, validationSeatsById, ticketTypesById, _dateTimeProvider.UtcNow) is { } earlyConflict)
        {
            return Result<Guid>.Failure(earlyConflict);
        }

        timings.TransactionStarting = Stopwatch.GetTimestamp();
        await using var transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);
        timings.TransactionBegun = Stopwatch.GetTimestamp();

        // Queue Mode 切換的線性化時點：重新鎖定並讀取 Event，以鎖定後讀到的 IsQueueModeEnabled 為唯一
        // 採信依據，不得沿用上面交易前、未加鎖的 orderEvent（rate-limiting-queue design.md 決策 4）。
        // 這個鎖定對每筆訂單都會執行，不論該活動是否曾被判斷為未開啟排隊。鎖定順序固定為
        // Event → PurchaseQueueEntry → EventSeat → TicketType。
        // 用共享鎖就足夠（order-placement-p95-phase2 design.md 決策 1）：下單只讀不改 Event，共享鎖彼此相容，同一活動的下單
        // 不再逐筆通過；切換排隊模式的 FOR UPDATE 與共享鎖互斥，持有期間切換無法提交，切換先提交時這裡讀到新值。
        // 超賣由下面座位／票種各自的 FOR UPDATE 保護，從來不依賴 Event 鎖。
        var lockedEvent = await _eventRepository.GetForShareAsync(distinctEventIds[0], cancellationToken);
        timings.EventLocked = Stopwatch.GetTimestamp();
        if (lockedEvent is null)
        {
            return Result<Guid>.Failure(Error.NotFound($"Event '{distinctEventIds[0]}' was not found."));
        }

        // now 必須在 GetForShareAsync 返回之後取得：等鎖期間可能跨過開賣／停售時點，用等鎖前的時間會誤判。
        // 失敗時直接返回，交易未 commit 即在 dispose 時回滾（IUnitOfWorkTransaction 契約）。
        if (EventSalesWindowErrors.GetErrorOrNull(lockedEvent, _dateTimeProvider.UtcNow) is { } salesError)
        {
            return Result<Guid>.Failure(salesError);
        }

        // 實名閘門補位：交易外讀不到活動時主要檢查被跳過，這裡以 lockedEvent 補上，必須在排隊資格與任何座位／庫存鎖定之前。
        // 兩次都讀到但旗標不同代表 I1 被破壞，不靜默採信任一方（design.md 決策 3）。
        if (orderEvent is null)
        {
            if (lockedEvent.IsRealNameRequired &&
                await _memberRealNameRepository.GetAsync(buyerId, cancellationToken) is null)
            {
                return Result<Guid>.Failure(Error.RealNameRequired($"Event '{lockedEvent.Id}' requires real-name registration."));
            }

            // 座位圖成員與分區比對補位：必須在排隊資格與任何座位／庫存鎖定之前（design.md 決策 4）。
            if (GetSeatPlacementErrorOrNull(seatPlacements, lockedEvent.SeatMapId) is { } lockedSeatPlacementError)
            {
                return Result<Guid>.Failure(lockedSeatPlacementError);
            }
        }
        else if (orderEvent.IsRealNameRequired != lockedEvent.IsRealNameRequired)
        {
            throw new InvalidOperationException($"Event '{lockedEvent.Id}' IsRealNameRequired changed between reads (invariant I1 violated).");
        }

        PurchaseQueueEntry? queueEntry = null;
        if (lockedEvent.IsQueueModeEnabled)
        {
            // 只依 Status 過濾並鎖定，不比較 AdmissionExpiresAtUtc；OrderService 自行以時間重新確認資格。
            queueEntry = await _purchaseQueueRepository.GetForUpdateAsync(lockedEvent.Id, buyerId, cancellationToken);
            var isAdmitted = queueEntry is { Status: PurchaseQueueEntryStatus.Admitted } &&
                queueEntry.AdmissionExpiresAtUtc > _dateTimeProvider.UtcNow;
            if (!isAdmitted)
            {
                // MUST NOT 呼叫 Expire()：狀態落地寫入統一交由背景服務／JoinPurchaseQueueHandler 自我修復
                // 負責，OrderService 只讀取判斷（design.md 決策 4）。
                return Result<Guid>.Failure(
                    Error.QueueAdmissionRequired($"Event '{lockedEvent.Id}' requires purchase queue admission."));
            }
        }

        // 鎖定順序（design.md 決策 3 固定規則）：先鎖座位，後鎖票種；純計數訂單沒有任何座位時，
        // MUST NOT 呼叫 GetForUpdateAsync(空清單)（該方法對空清單拋 ArgumentException）。
        // .Distinct() 比照下面 countingTicketTypeIds／ChangeOrderStatusAsync 既有的相同模式：
        // GetForUpdateAsync 內部會對重複 id 去重後才查詢（EventSeatRepository.GetForUpdateAsync），
        // 若這裡傳入未去重的清單，查回的筆數會比傳入的筆數少，讓下面的 Count 比對誤判為座位不存在
        // （hardener 稽核抓到：目前 PlaceOrderRequestValidator 已擋掉重複 EventSeatId，這裡不會被觸發，
        // 但這個保護在另一個檔案，這裡本身沒有防禦，跟檔案內其他四處同類型檢查的既有模式不一致）。
        var eventSeatIds = request.Selections.Where(s => s.EventSeatId.HasValue).Select(s => s.EventSeatId!.Value).Distinct().ToList();
        var eventSeatsById = new Dictionary<Guid, EventSeat>();
        if (eventSeatIds.Count > 0)
        {
            var eventSeats = await _eventSeatRepository.GetForUpdateAsync(eventSeatIds, cancellationToken);
            if (eventSeats.Count < eventSeatIds.Count)
            {
                return Result<Guid>.Failure(Error.NotFound("One or more selected seats were not found."));
            }

            eventSeatsById = eventSeats.ToDictionary(es => es.Id);
        }

        // 只有純計數（RequiresSeat = false）票種需要鎖定：座位模式票種的庫存概念是 EventSeat 狀態機，
        // 不涉及 AvailableQuantity。TicketType 之間依 Id 排序鎖定（GetForUpdateAsync 內部保證），
        // 避免一筆訂單同時買多個不同計數票種時，跟其他並發交易鎖定順序不一致造成死鎖。
        var countingTicketTypeIds = request.Selections
            .Where(s => !s.EventSeatId.HasValue)
            .Select(s => s.TicketTypeId)
            .Distinct()
            .ToList();
        var lockedTicketTypesById = new Dictionary<Guid, TicketType>();
        if (countingTicketTypeIds.Count > 0)
        {
            var lockedTicketTypes = await _ticketTypeRepository.GetForUpdateAsync(countingTicketTypeIds, cancellationToken);
            if (lockedTicketTypes.Count < countingTicketTypeIds.Count)
            {
                return Result<Guid>.Failure(Error.NotFound("One or more selected ticket types were not found."));
            }

            lockedTicketTypesById = lockedTicketTypes.ToDictionary(t => t.Id);
        }

        var seatSelections = new List<SeatSelection>();
        var quantitySelections = new List<QuantitySelection>();

        foreach (var selectionRequest in request.Selections)
        {
            var ticketType = ticketTypesById[selectionRequest.TicketTypeId];

            if (!selectionRequest.EventSeatId.HasValue)
            {
                // 計數項目：Reserve() MUST 對 GetForUpdateAsync 回傳的鎖定實例呼叫，不可用上面
                // no-tracking 查到的 ticketType（design.md 決策 3）。
                quantitySelections.Add(new QuantitySelection(lockedTicketTypesById[ticketType.Id], selectionRequest.Quantity));
                continue;
            }

            if (!eventSeatsById.TryGetValue(selectionRequest.EventSeatId.Value, out var eventSeat))
            {
                return Result<Guid>.Failure(Error.NotFound($"Seat '{selectionRequest.EventSeatId}' was not found."));
            }

            if (ticketType.EventId != eventSeat.EventId)
            {
                return Result<Guid>.Failure(Error.Validation("Ticket type does not belong to the same event as the selected seat."));
            }

            // 座位圖成員與分區已在交易前（或鎖內補位）比對過，鎖內不再重讀活動與座位圖（design.md 決策 4）。
            seatSelections.Add(new SeatSelection(eventSeat, ticketType));
        }

        var result = _createOrderHandler.Handle(buyerId, seatSelections, quantitySelections);
        if (!result.IsSuccess)
        {
            return Result<Guid>.Failure(result.Error!);
        }

        // 有效名額的立即釋放語意：Complete() 讓名額在交易提交的當下即釋放，不延遲到 AdmissionExpiresAtUtc
        // 才釋放（design.md 決策 4）。
        queueEntry?.Complete();

        _orderRepository.Add(result.Value!);
        timings.CommitStarting = Stopwatch.GetTimestamp();
        await transaction.CommitAsync(cancellationToken);
        timings.Committed = Stopwatch.GetTimestamp();
        // 之後的 Redis 呼叫與資料庫無關，先歸還連線，不讓 Redis 延遲占住連線池。
        await connection.DisposeAsync();

        // 訂單完成同步移除 Redis admitted 鏡像，交易 commit 後才執行、非同一交易，best-effort
        // （purchase-queue-redis-admission design.md Decision 8）。
        if (queueEntry is not null)
        {
            await _admissionMirror.SyncCompletionAsync(lockedEvent.Id, queueEntry.Id, cancellationToken);
        }

        // 只有純計數票種項目才會呼叫 TicketType.Reserve、變更 AvailableQuantity；純座位制訂單
        // （quantitySelections 為空）不觸發票種列表快取失效（design.md 決策 4 訂正）。
        if (quantitySelections.Count > 0)
        {
            await _queryCache.RemoveAsync(GetTicketTypesHandler.BuildCacheKey(distinctEventIds[0]), cancellationToken);
        }

        return Result<Guid>.Success(result.Value!.Id);
    }

    public async Task<Result> ConfirmOrderAsync(Guid orderId, Guid requestingBuyerId, CancellationToken cancellationToken)
    {
        // 確認付款不呼叫 TicketType.Reserve/Release，AvailableQuantity 在這一步不變，
        // 不觸發票種列表快取失效（design.md 決策 4）。
        var result = await ChangeOrderStatusAsync(
            orderId, requestingBuyerId, _confirmOrderHandler.Handle, invalidatesTicketTypeCache: false, cancellationToken);
        if (!result.IsSuccess)
        {
            return result;
        }

        try
        {
            var order = await _orderRepository.GetByIdAsync(orderId, cancellationToken);
            var @event = order is null ? null : await _eventRepository.GetByIdAsync(order.EventId, cancellationToken);
            var buyer = order is null ? null : await _dbContext.Members.FirstOrDefaultAsync(m => m.Id == order.BuyerId, cancellationToken);
            var content = TicketIssuedNotificationContentFactory.Create(orderId, order, @event, buyer);

            await _emailNotificationService.NotifyTicketsIssuedAsync(
                content.ToEmail, content.EventTitle, content.OrderId, content.TicketCount, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 只有「這次呼叫傳入的 cancellationToken 本身被觸發」才視為呼叫端主動取消（例如連線中斷），
            // 不記錄為 Error——比照既有 ExpiredOrderCleanupService 對 OperationCanceledException 的既有
            // 處理慣例。任何其他來源的 OperationCanceledException（例如 Email provider 自己的 timeout、
            // provider 內部用了另一個 token）不滿足這個 when 條件，會繼續往下落入 catch (Exception)，
            // 視為真正的通知失敗記錄下來（見 email-notification design.md 決策 2）。訂單確認結果此時已經
            // 確定為成功，直接放行，不重新拋出。
        }
        catch (Exception exception)
        {
            // 這個 catch 同時涵蓋「重新查詢 Order/Event/Member 失敗或缺漏」（含上面 Factory 丟出的
            // InvalidOperationException）與「呼叫通知服務本身失敗」兩種情況（見 design.md 決策 3），
            // log 訊息刻意不區分兩者——多加一層判斷失敗發生在查詢或寄送哪個階段，對「best-effort、
            // 記錄後即放行」這個語意沒有額外價值，例外本身（含 Factory 丟出的明確訊息）加上
            // {OrderId} 已足以讓人之後手動追查。
            _logger.LogError(exception, "Failed to prepare or send ticket-issued notification for order {OrderId}.", orderId);
        }

        return result;
    }

    public Task<Result> CancelOrderAsync(Guid orderId, Guid requestingBuyerId, CancellationToken cancellationToken)
        => ChangeOrderStatusAsync(orderId, requestingBuyerId, WrapSync(_cancelOrderHandler.Handle), invalidatesTicketTypeCache: true, cancellationToken);

    /// <summary>
    /// 背景清理呼叫，沒有買家身份可驗證，改以「訂單確實已逾時」作為授權依據，取代本人驗證
    /// （見 ticketing-order-management design.md 決策 1）。
    /// </summary>
    public Task<Result> CancelExpiredOrderAsync(Guid orderId, CancellationToken cancellationToken)
        => ChangeOrderStatusAsync(orderId, requestingBuyerId: null, WrapSync(_cancelOrderHandler.Handle), invalidatesTicketTypeCache: true, cancellationToken);

    private sealed record SeatPlacement(EventSeat EventSeat, Seat SeatTemplate, TicketType TicketType);

    /// <summary>
    /// 先比對所有座位的座位圖成員（404，訊息沿用原本鎖內「樣板不在座位圖」的訊息），再比對分區（400）：
    /// 座位實際所屬分區 MUST 與所選票種一致，防止用低價分區的票種配高價分區的座位（ticketing-purchase design.md 決策 2 第 4 點）。
    /// </summary>
    private static Error? GetSeatPlacementErrorOrNull(IReadOnlyList<SeatPlacement> seatPlacements, Guid eventSeatMapId)
    {
        if (seatPlacements.FirstOrDefault(p => p.SeatTemplate.SeatMapId != eventSeatMapId) is { } foreignPlacement)
        {
            return Error.NotFound($"Seat '{foreignPlacement.EventSeat.SeatId}' was not found in the seat map.");
        }

        if (seatPlacements.FirstOrDefault(p => p.SeatTemplate.ZoneCode != p.TicketType.ZoneCode) is { } mismatchedPlacement)
        {
            return Error.Validation(
                $"Seat '{mismatchedPlacement.EventSeat.Id}' belongs to zone '{mismatchedPlacement.SeatTemplate.ZoneCode}', which does not match ticket type zone '{mismatchedPlacement.TicketType.ZoneCode}'.");
        }

        return null;
    }

    /// <summary>
    /// 判斷順序與 <see cref="CreateOrderHandler"/> 相同（先座位、後計數票種，各依請求順序），被拒時訊息才會與鎖內一致。
    /// 座位只透過 <see cref="EventSeat.IsAvailableForHold"/> 判斷，逾時的暫扣視同可售。
    /// </summary>
    private static Error? GetEarlyConflictOrNull(
        PlaceOrderRequest request, IReadOnlyDictionary<Guid, EventSeat> seatsById, IReadOnlyDictionary<Guid, TicketType> ticketTypesById, DateTime now)
    {
        var unavailableSeatId = request.Selections
            .Where(s => s.EventSeatId.HasValue)
            .Select(s => s.EventSeatId!.Value)
            .FirstOrDefault(id => !seatsById[id].IsAvailableForHold(now));
        if (unavailableSeatId != Guid.Empty)
        {
            return PlaceOrderConflictErrors.SeatNoLongerAvailable(unavailableSeatId);
        }

        // PlaceOrderRequestValidator 已擋掉重複的計數票種，GroupBy 加總只是不依賴另一個檔案的防禦；long 累加理由同限購檢查。
        var insufficientTicketTypeId = request.Selections
            .Where(s => !s.EventSeatId.HasValue)
            .GroupBy(s => s.TicketTypeId)
            .FirstOrDefault(g => ticketTypesById[g.Key].AvailableQuantity < g.Sum(s => (long)s.Quantity))
            ?.Key;
        return insufficientTicketTypeId is { } ticketTypeId ? PlaceOrderConflictErrors.TicketTypeInventoryInsufficient(ticketTypeId) : null;
    }

    // CancelOrderHandler.Handle 維持同步（純記憶體邏輯，無 I/O），包一層轉成跟 ConfirmOrderHandler.Handle
    // 相同的非同步委派型別，讓兩者能共用同一套 ChangeOrderStatusAsync 交易骨架（見 design.md 決策 3）。
    private static Func<Order, IReadOnlyDictionary<Guid, EventSeat>, IReadOnlyDictionary<Guid, TicketType>, CancellationToken, Task<Result>> WrapSync(
        Func<Order, IReadOnlyDictionary<Guid, EventSeat>, IReadOnlyDictionary<Guid, TicketType>, Result> syncHandle)
        => (order, seats, ticketTypes, _) => Task.FromResult(syncHandle(order, seats, ticketTypes));

    private async Task<Result> ChangeOrderStatusAsync(
        Guid orderId,
        Guid? requestingBuyerId,
        Func<Order, IReadOnlyDictionary<Guid, EventSeat>, IReadOnlyDictionary<Guid, TicketType>, CancellationToken, Task<Result>> handle,
        bool invalidatesTicketTypeCache,
        CancellationToken cancellationToken)
    {
        var order = await _orderRepository.GetByIdAsync(orderId, cancellationToken);
        if (order is null)
        {
            return Result.Failure(Error.NotFound($"Order '{orderId}' was not found."));
        }

        if (requestingBuyerId is not null)
        {
            if (order.BuyerId != requestingBuyerId)
            {
                return Result.Failure(Error.Forbidden("You are not the buyer of this order."));
            }
        }
        else if (_dateTimeProvider.UtcNow < order.HeldUntilUtc)
        {
            // 系統呼叫（背景清理），沒有買家身份可驗證；用「訂單確實已逾時」取代本人驗證作為授權依據，
            // 避免這個方法被誤用成可以繞過買家授權、取消任何 Pending 訂單的工具
            // （見 ticketing-order-management design.md 決策 1）。
            return Result.Failure(Error.Conflict($"Order '{orderId}' is not yet expired."));
        }

        await using var transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);

        // 鎖定順序（design.md 決策 3）：先鎖座位、後鎖票種；純計數訂單（沒有任何座位行項）
        // MUST NOT 呼叫 GetForUpdateAsync(空清單)。
        var eventSeatIds = order.Items.Where(i => i.EventSeatId.HasValue).Select(i => i.EventSeatId!.Value).Distinct().ToList();
        var eventSeatsById = new Dictionary<Guid, EventSeat>();
        if (eventSeatIds.Count > 0)
        {
            var eventSeats = await _eventSeatRepository.GetForUpdateAsync(eventSeatIds, cancellationToken);
            if (eventSeats.Count < eventSeatIds.Count)
            {
                // 理論上不應該發生：目前系統沒有刪除 EventSeat 的路徑，order.Items 引用的座位建立後就一直存在。
                // 用 NotFound 而非 Conflict，跟 ConfirmOrderHandler 自己對「查不到座位」的既有分類一致
                // （見 src/ProjectC.Application/Orders/ConfirmOrderHandler.cs），維持 Confirm/Cancel 行為對稱。
                return Result.Failure(Error.NotFound($"One or more seats referenced by order '{orderId}' were not found."));
            }

            eventSeatsById = eventSeats.ToDictionary(es => es.Id);
        }

        // 計數行項對應的 TicketType 也要鎖：即使是 Confirm（不寫入 AvailableQuantity），純計數訂單
        // 在資料庫層唯一的序列化點就是這個鎖，省略會讓兩個並發 Confirm 都通過、造成重複收款
        // （design.md 決策 3 審查後補充的說明）。
        var ticketTypeIds = order.Items.Where(i => !i.EventSeatId.HasValue).Select(i => i.TicketTypeId!.Value).Distinct().ToList();
        var ticketTypesById = new Dictionary<Guid, TicketType>();
        if (ticketTypeIds.Count > 0)
        {
            var ticketTypes = await _ticketTypeRepository.GetForUpdateAsync(ticketTypeIds, cancellationToken);
            if (ticketTypes.Count < ticketTypeIds.Count)
            {
                return Result.Failure(Error.NotFound($"One or more ticket types referenced by order '{orderId}' were not found."));
            }

            ticketTypesById = ticketTypes.ToDictionary(t => t.Id);
        }

        // 鎖後重讀，避免兩個並發的同類操作（尤其是兩個並發 Cancel）其中一個誤報成功
        // （見 ticketing-purchase design.md 決策 3，不可省略）。
        await _orderRepository.ReloadAsync(order, cancellationToken);

        var result = await handle(order, eventSeatsById, ticketTypesById, cancellationToken);
        if (!result.IsSuccess)
        {
            return result;
        }

        await transaction.CommitAsync(cancellationToken);

        if (invalidatesTicketTypeCache)
        {
            await _queryCache.RemoveAsync(GetTicketTypesHandler.BuildCacheKey(order.EventId), cancellationToken);
        }

        return result;
    }
}
