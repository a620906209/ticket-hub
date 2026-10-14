using FluentAssertions;
using ProjectC.Application.Orders.GetOrders;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Events;
using ProjectC.Domain.Members;
using ProjectC.Domain.Orders;

namespace ProjectC.Application.Tests.Orders.GetOrders;

public class GetOrdersHandlerTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly Guid OrganizerId = Guid.NewGuid();

    private readonly FakeOrderRepository _orderRepository = new();
    private readonly FakeMemberDisplayNameReader _memberDisplayNameReader = new();
    private readonly FakeEventRepository _eventRepository = new();

    /// <summary>活動同時放進 Fake event repository，否則買家缺失等測試會先因活動查不到而丟出錯的例外（tasks.md 4.0）。</summary>
    private Event SeedEvent(Guid organizerId, string title = "Test Event")
    {
        var @event = new Event(Guid.NewGuid(), title, Now.AddDays(30), Guid.NewGuid(), Guid.NewGuid(), organizerId);
        _eventRepository.Data.Add(@event);
        _orderRepository.OrganizerIdByEventId[@event.Id] = organizerId;
        return @event;
    }

    private Order SeedOrder(Guid organizerId, DateTime heldUntilUtc, Guid? buyerId = null, string? buyerDisplayName = null)
    {
        var @event = SeedEvent(organizerId);
        var order = new Order(
            Guid.NewGuid(), @event.Id, buyerId ?? Guid.NewGuid(), heldUntilUtc.AddMinutes(-10), heldUntilUtc,
            [new OrderItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 500m)]);
        return AddOrder(order, buyerDisplayName);
    }

    private Order AddOrder(Order order, string? buyerDisplayName = null)
    {
        _memberDisplayNameReader.DisplayNamesByMemberId[order.BuyerId] = buyerDisplayName ?? $"Buyer {order.BuyerId:N}";
        _orderRepository.Data.Add(order);
        return order;
    }

    private static Order CreateOrder(Guid eventId, DateTime createdAtUtc, Guid? orderId = null, DateTime? heldUntilUtc = null)
        => new(
            orderId ?? Guid.NewGuid(), eventId, Guid.NewGuid(), createdAtUtc, heldUntilUtc ?? createdAtUtc.AddMinutes(10),
            [new OrderItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 500m)]);

    private GetOrdersHandler CreateHandler(IMemberDisplayNameReader? memberDisplayNameReader = null)
        => new(_orderRepository, memberDisplayNameReader ?? _memberDisplayNameReader, _eventRepository, new FakeDateTimeProvider { UtcNow = Now });

    // [ORD-LIST-001]
    [Fact]
    public async Task HandleAsync_ReturnsCallerOrganizerOrdersWithLiveStatus()
    {
        var pendingOrder = SeedOrder(OrganizerId, Now.AddMinutes(10), buyerDisplayName: "Alice");
        var expiredOrder = SeedOrder(OrganizerId, Now.AddMinutes(-1), buyerDisplayName: "Bob");
        var otherOrganizerOrder = SeedOrder(Guid.NewGuid(), Now.AddMinutes(10));

        var result = await CreateHandler().HandleAsync(OrganizerId, CancellationToken.None);

        result.Should().HaveCount(2);
        result.Should().NotContain(o => o.Id == otherOrganizerOrder.Id);
        result.Should().ContainSingle(o => o.Id == pendingOrder.Id && o.Status == "Pending" && o.BuyerDisplayName == "Alice");
        // 已逾時但持久化狀態仍是 Pending 的訂單，即時狀態 MUST 回報 Expired，不是持久化欄位本身。
        result.Should().ContainSingle(o => o.Id == expiredOrder.Id && o.Status == "Expired" && o.BuyerDisplayName == "Bob");
    }

    [Fact]
    public async Task HandleAsync_WhenNoOrders_ReturnsEmptyList()
    {
        var result = await CreateHandler().HandleAsync(OrganizerId, CancellationToken.None);

        result.Should().BeEmpty();
        // 比照 GetMyOrdersHandler：0 筆訂單時不發出買家名稱查詢（repository 的「空清單不查詢」只是額外防線）。
        _memberDisplayNameReader.GetDisplayNamesByIdsCallCount.Should().Be(0);
        _eventRepository.GetByIdsCallCount.Should().Be(0);
    }

    // [ORD-LIST-002] 單次批次查詢不重複買家；DTO 本身沒有 Email 欄位，回應不含 Email 由整合測試驗證序列化結果。
    [Fact]
    public async Task HandleAsync_WithTwoBuyers_ReturnsBuyerDisplayNamesWithSingleDistinctBatchQuery()
    {
        var aliceId = Guid.NewGuid();
        var bobId = Guid.NewGuid();
        var aliceFirstOrder = SeedOrder(OrganizerId, Now.AddMinutes(10), aliceId, "Alice");
        var aliceSecondOrder = SeedOrder(OrganizerId, Now.AddMinutes(10), aliceId, "Alice");
        var bobOrder = SeedOrder(OrganizerId, Now.AddMinutes(10), bobId, "Bob");

        var result = await CreateHandler().HandleAsync(OrganizerId, CancellationToken.None);

        result.Single(o => o.Id == aliceFirstOrder.Id).BuyerDisplayName.Should().Be("Alice");
        result.Single(o => o.Id == aliceSecondOrder.Id).BuyerDisplayName.Should().Be("Alice");
        result.Single(o => o.Id == bobOrder.Id).BuyerDisplayName.Should().Be("Bob");
        _memberDisplayNameReader.GetDisplayNamesByIdsCallCount.Should().Be(1);
        _memberDisplayNameReader.LastGetDisplayNamesByIdsIds.Should().BeEquivalentTo([aliceId, bobId]);
    }

    // [ORD-LIST-003] 與 GetAdminEventsHandler 對可為 null 的 CreatedByMemberId 回 null 不同：BuyerId 查不到即資料損毀。
    [Fact]
    public async Task HandleAsync_WhenBuyerMissing_ThrowsWithOrderAndBuyerIds()
    {
        SeedOrder(OrganizerId, Now.AddMinutes(10));
        var orphanOrder = SeedOrder(OrganizerId, Now.AddMinutes(10));
        _memberDisplayNameReader.DisplayNamesByMemberId.Remove(orphanOrder.BuyerId);

        var act = () => CreateHandler().HandleAsync(OrganizerId, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain(orphanOrder.Id.ToString()).And.Contain(orphanOrder.BuyerId.ToString());
    }

    // 回傳筆數與需求相同、但缺的是其中一個 Id 時仍要判定缺失，不以筆數比較（design.md 決策 4）。
    [Fact]
    public async Task HandleAsync_WhenReaderReturnsSameCountButWrongId_ThrowsWithMissingBuyerId()
    {
        SeedOrder(OrganizerId, Now.AddMinutes(10));
        var secondOrder = SeedOrder(OrganizerId, Now.AddMinutes(10));
        var reader = new SubstitutingMemberDisplayNameReader(_memberDisplayNameReader, secondOrder.BuyerId, Guid.NewGuid());

        var act = () => CreateHandler(reader).HandleAsync(OrganizerId, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain(secondOrder.BuyerId.ToString());
    }

    [Fact]
    public async Task HandleAsync_WithCallerToken_ForwardsTokenToEveryQuery()
    {
        SeedOrder(OrganizerId, Now.AddMinutes(10));
        using var cancellationTokenSource = new CancellationTokenSource();
        var token = cancellationTokenSource.Token;

        await CreateHandler().HandleAsync(OrganizerId, token);

        _orderRepository.LastGetByOrganizerIdToken.Should().Be(token);
        _memberDisplayNameReader.LastGetDisplayNamesByIdsToken.Should().Be(token);
        _eventRepository.LastGetByIdsToken.Should().Be(token);
    }

    // [ORD-LIST-005] 第二筆的單一計數項目 Quantity 3：防止 TicketCount 以項目筆數計算、TotalAmount 忽略 Quantity。
    [Fact]
    public async Task HandleAsync_ReturnsEventTitleTicketCountTotalAmountAndCreatedAt()
    {
        var createdAtUtc = Now.AddMinutes(-3);
        var springConcert = SeedEvent(OrganizerId, "春季音樂會");
        var summerFestival = SeedEvent(OrganizerId, "夏日祭典");
        var twoSeatOrder = AddOrder(new Order(
            Guid.NewGuid(), springConcert.Id, Guid.NewGuid(), createdAtUtc, createdAtUtc.AddMinutes(10),
            [
                new OrderItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 1000m),
                new OrderItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 1500m),
            ]));
        var countingOrder = AddOrder(new Order(
            Guid.NewGuid(), summerFestival.Id, Guid.NewGuid(), createdAtUtc.AddMinutes(-1), createdAtUtc.AddMinutes(9),
            [new OrderItem(Guid.NewGuid(), Guid.NewGuid(), null, 3, 800m)]));

        var result = await CreateHandler().HandleAsync(OrganizerId, CancellationToken.None);

        var twoSeatSummary = result.Single(o => o.Id == twoSeatOrder.Id);
        twoSeatSummary.EventTitle.Should().Be("春季音樂會");
        twoSeatSummary.TicketCount.Should().Be(2);
        twoSeatSummary.TotalAmount.Should().Be(2500m);
        twoSeatSummary.CreatedAtUtc.Should().Be(createdAtUtc);
        var countingSummary = result.Single(o => o.Id == countingOrder.Id);
        countingSummary.EventTitle.Should().Be("夏日祭典");
        countingSummary.TicketCount.Should().Be(3);
        countingSummary.TotalAmount.Should().Be(2400m);
        countingSummary.CreatedAtUtc.Should().Be(createdAtUtc.AddMinutes(-1));
        // 活動名稱比照買家名稱：單次批次查詢不重複活動（ORD-LIST-004 的查詢次數由整合測試驗證）。
        _eventRepository.GetByIdsCallCount.Should().Be(1);
        _eventRepository.LastGetByIdsIds.Should().BeEquivalentTo([springConcert.Id, summerFestival.Id]);
    }

    // [ORD-LIST-006] Fake 以 T2、T1、T3 的順序回傳（非建立時間順序），結果必須是 T3、T2、T1。
    [Fact]
    public async Task HandleAsync_SortsByCreatedAtDescending()
    {
        var @event = SeedEvent(OrganizerId);
        // 最早建立的訂單給最晚的 HeldUntilUtc：若誤以 HeldUntilUtc 排序，它會排到第一而失敗——
        // 決策 5 選 CreatedAtUtc 正是為了持有時間日後可變時仍依建立時間排序。
        var t1Order = CreateOrder(@event.Id, Now.AddMinutes(-30), heldUntilUtc: Now.AddMinutes(60));
        var t2Order = CreateOrder(@event.Id, Now.AddMinutes(-20));
        var t3Order = CreateOrder(@event.Id, Now.AddMinutes(-10));
        AddOrder(t2Order);
        AddOrder(t1Order);
        AddOrder(t3Order);

        var result = await CreateHandler().HandleAsync(OrganizerId, CancellationToken.None);

        result.Select(o => o.Id).Should().Equal(t3Order.Id, t2Order.Id, t1Order.Id);
        // Requirement 要求以「不重複」的活動 Id 批次查詢：三筆訂單同一活動，只送出一個 Id。
        _eventRepository.LastGetByIdsIds.Should().Equal(@event.Id);
    }

    // [ORD-LIST-008] 建立時間相同時，不論 repository 回傳順序為何，都依 Id 升冪排列。
    [Fact]
    public async Task HandleAsync_WhenCreatedAtTies_SortsByIdAscendingRegardlessOfRepositoryOrder()
    {
        var @event = SeedEvent(OrganizerId);
        var smallerIdOrder = CreateOrder(@event.Id, Now.AddMinutes(-5), Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var largerIdOrder = CreateOrder(@event.Id, Now.AddMinutes(-5), Guid.Parse("00000000-0000-0000-0000-000000000002"));
        AddOrder(largerIdOrder);
        AddOrder(smallerIdOrder);

        var firstResult = await CreateHandler().HandleAsync(OrganizerId, CancellationToken.None);
        _orderRepository.Data.Reverse();
        var secondResult = await CreateHandler().HandleAsync(OrganizerId, CancellationToken.None);

        firstResult.Select(o => o.Id).Should().Equal(smallerIdOrder.Id, largerIdOrder.Id);
        secondResult.Select(o => o.Id).Should().Equal(smallerIdOrder.Id, largerIdOrder.Id);
    }

    // [ORD-LIST-007] 所有買家都存在、只有活動查不到：例外必須是活動缺失（帶 OrderId／EventId），不是買家缺失。
    [Fact]
    public async Task HandleAsync_WhenEventMissing_ThrowsWithOrderAndEventIds()
    {
        SeedOrder(OrganizerId, Now.AddMinutes(10));
        var orphanOrder = SeedOrder(OrganizerId, Now.AddMinutes(10));
        _eventRepository.Data.RemoveAll(e => e.Id == orphanOrder.EventId);

        var act = () => CreateHandler().HandleAsync(OrganizerId, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain(orphanOrder.Id.ToString())
            .And.Contain(orphanOrder.EventId.ToString())
            .And.NotContain(orphanOrder.BuyerId.ToString());
    }

    [Fact]
    public async Task HandleAsync_WhenReaderIsCancelled_PropagatesOperationCanceledException()
    {
        SeedOrder(OrganizerId, Now.AddMinutes(10));

        var act = () => CreateHandler(new CancellingMemberDisplayNameReader()).HandleAsync(OrganizerId, CancellationToken.None);

        await act.Should().ThrowExactlyAsync<OperationCanceledException>();
    }

    private sealed class SubstitutingMemberDisplayNameReader(IMemberDisplayNameReader inner, Guid removedMemberId, Guid extraMemberId) : IMemberDisplayNameReader
    {
        public async Task<IReadOnlyDictionary<Guid, string>> GetDisplayNamesByIdsAsync(IReadOnlyList<Guid> memberIds, CancellationToken cancellationToken)
        {
            var displayNames = (await inner.GetDisplayNamesByIdsAsync(memberIds, cancellationToken))
                .Where(pair => pair.Key != removedMemberId)
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            displayNames[extraMemberId] = "Unrelated";
            return displayNames;
        }
    }

    private sealed class CancellingMemberDisplayNameReader : IMemberDisplayNameReader
    {
        public Task<IReadOnlyDictionary<Guid, string>> GetDisplayNamesByIdsAsync(IReadOnlyList<Guid> memberIds, CancellationToken cancellationToken)
            => throw new OperationCanceledException();
    }
}
