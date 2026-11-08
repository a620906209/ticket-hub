using FluentAssertions;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Paging;
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

    private static readonly PageRequest FirstPage = new(PageRequest.DefaultPage, PageRequest.DefaultPageSize);

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
        => new(_orderRepository, memberDisplayNameReader ?? _memberDisplayNameReader, _eventRepository, new FakeDateTimeProvider { UtcNow = Now }, new PageRequestValidator());

    // [ORD-LIST-001]
    [Fact]
    public async Task HandleAsync_ReturnsCallerOrganizerOrdersWithLiveStatus()
    {
        var pendingOrder = SeedOrder(OrganizerId, Now.AddMinutes(10), buyerDisplayName: "Alice");
        var expiredOrder = SeedOrder(OrganizerId, Now.AddMinutes(-1), buyerDisplayName: "Bob");
        var otherOrganizerOrder = SeedOrder(Guid.NewGuid(), Now.AddMinutes(10));

        var result = (await CreateHandler().HandleAsync(OrganizerId, FirstPage, CancellationToken.None)).Value!.Items;

        result.Should().HaveCount(2);
        result.Should().NotContain(o => o.Id == otherOrganizerOrder.Id);
        result.Should().ContainSingle(o => o.Id == pendingOrder.Id && o.Status == "Pending" && o.BuyerDisplayName == "Alice");
        // 已逾時但持久化狀態仍是 Pending 的訂單，即時狀態 MUST 回報 Expired，不是持久化欄位本身。
        result.Should().ContainSingle(o => o.Id == expiredOrder.Id && o.Status == "Expired" && o.BuyerDisplayName == "Bob");
    }

    [Fact]
    public async Task HandleAsync_WhenNoOrders_ReturnsEmptyList()
    {
        var result = (await CreateHandler().HandleAsync(OrganizerId, FirstPage, CancellationToken.None)).Value!.Items;

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

        var result = (await CreateHandler().HandleAsync(OrganizerId, FirstPage, CancellationToken.None)).Value!.Items;

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

        var act = () => CreateHandler().HandleAsync(OrganizerId, FirstPage, CancellationToken.None);

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

        var act = () => CreateHandler(reader).HandleAsync(OrganizerId, FirstPage, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain(secondOrder.BuyerId.ToString());
    }

    [Fact]
    public async Task HandleAsync_WithCallerToken_ForwardsTokenToEveryQuery()
    {
        SeedOrder(OrganizerId, Now.AddMinutes(10));
        using var cancellationTokenSource = new CancellationTokenSource();
        var token = cancellationTokenSource.Token;

        await CreateHandler().HandleAsync(OrganizerId, FirstPage, token);

        _orderRepository.LastGetPageByOrganizerIdToken.Should().Be(token);
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

        var result = (await CreateHandler().HandleAsync(OrganizerId, FirstPage, CancellationToken.None)).Value!.Items;

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

    // [ORD-PAGE-001 / LP-RESP-001] 第二頁：envelope 欄位、總筆數為範圍內全部、買家與活動只針對當頁查詢。
    [Fact]
    public async Task HandleAsync_WithSecondPage_ReturnsThatPageWithEnvelopeAndQueriesOnlyPageIds()
    {
        var other = SeedOrder(Guid.NewGuid(), Now.AddMinutes(10));
        var orders = Enumerable.Range(0, 5)
            .Select(i => SeedOrder(OrganizerId, Now.AddMinutes(10 + i)))
            .ToList();
        // 新到舊（CreatedAtUtc = heldUntil - 10 分）：orders[4]、[3] | [2]、[1] | [0]
        var page = (await CreateHandler().HandleAsync(OrganizerId, new PageRequest(2, 2), CancellationToken.None)).Value!;

        page.Page.Should().Be(2);
        page.PageSize.Should().Be(2);
        page.TotalCount.Should().Be(5);
        page.Items.Select(o => o.Id).Should().Equal(orders[2].Id, orders[1].Id);
        page.Items.Should().NotContain(o => o.Id == other.Id);
        _memberDisplayNameReader.LastGetDisplayNamesByIdsIds.Should().BeEquivalentTo([orders[2].BuyerId, orders[1].BuyerId]);
        _eventRepository.LastGetByIdsIds.Should().BeEquivalentTo([orders[2].EventId, orders[1].EventId]);
        _orderRepository.LastSkip.Should().Be(2);
        _orderRepository.LastTake.Should().Be(2);
    }

    // [LP-RESP-003] 超過最後一頁：成功、空 items、實際總筆數，不發出名稱查詢。
    [Fact]
    public async Task HandleAsync_WhenPageBeyondLastPage_ReturnsEmptyItemsWithActualTotal()
    {
        SeedOrder(OrganizerId, Now.AddMinutes(10));

        var result = await CreateHandler().HandleAsync(OrganizerId, new PageRequest(4, 20), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Should().BeEmpty();
        result.Value.TotalCount.Should().Be(1);
        _memberDisplayNameReader.GetDisplayNamesByIdsCallCount.Should().Be(0);
        _eventRepository.GetByIdsCallCount.Should().Be(0);
    }

    // [LP-VALID-001/003/005/006] 越界回 Validation，Repository 未被呼叫。
    [Theory]
    [InlineData(1, 101, new[] { "PageSize" })]
    [InlineData(0, 20, new[] { "Page" })]
    [InlineData(10001, 20, new[] { "Page" })]
    [InlineData(0, 101, new[] { "Page", "PageSize" })]
    public async Task HandleAsync_WhenPageRequestOutOfRange_ReturnsValidationErrorWithoutQueryingRepository(int page, int pageSize, string[] expectedFields)
    {
        var result = await CreateHandler().HandleAsync(OrganizerId, new PageRequest(page, pageSize), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        result.Error.FieldErrors!.Keys.Should().BeEquivalentTo(expectedFields);
        _orderRepository.GetPageCallCount.Should().Be(0);
    }

    // [ORD-PAGE-004] 資料不一致只在包含該筆的頁面失敗，另一頁正常。
    [Fact]
    public async Task HandleAsync_WhenOrphanBuyerOnOtherPage_RequestedPageStillSucceeds()
    {
        var healthy = SeedOrder(OrganizerId, Now.AddMinutes(30));
        var orphan = SeedOrder(OrganizerId, Now.AddMinutes(10));
        _memberDisplayNameReader.DisplayNamesByMemberId.Remove(orphan.BuyerId);

        var firstPage = await CreateHandler().HandleAsync(OrganizerId, new PageRequest(1, 1), CancellationToken.None);
        var failingPage = () => CreateHandler().HandleAsync(OrganizerId, new PageRequest(2, 1), CancellationToken.None);

        firstPage.Value!.Items.Select(o => o.Id).Should().Equal(healthy.Id);
        await failingPage.Should().ThrowAsync<InvalidOperationException>();
    }

    // [ORD-LIST-007] 所有買家都存在、只有活動查不到：例外必須是活動缺失（帶 OrderId／EventId），不是買家缺失。
    [Fact]
    public async Task HandleAsync_WhenEventMissing_ThrowsWithOrderAndEventIds()
    {
        SeedOrder(OrganizerId, Now.AddMinutes(10));
        var orphanOrder = SeedOrder(OrganizerId, Now.AddMinutes(10));
        _eventRepository.Data.RemoveAll(e => e.Id == orphanOrder.EventId);

        var act = () => CreateHandler().HandleAsync(OrganizerId, FirstPage, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain(orphanOrder.Id.ToString())
            .And.Contain(orphanOrder.EventId.ToString())
            .And.NotContain(orphanOrder.BuyerId.ToString());
    }

    [Fact]
    public async Task HandleAsync_WhenReaderIsCancelled_PropagatesOperationCanceledException()
    {
        SeedOrder(OrganizerId, Now.AddMinutes(10));

        var act = () => CreateHandler(new CancellingMemberDisplayNameReader()).HandleAsync(OrganizerId, FirstPage, CancellationToken.None);

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
