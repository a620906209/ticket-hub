using FluentAssertions;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Paging;
using ProjectC.Application.Orders.GetMyOrders;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Events;
using ProjectC.Domain.Orders;

namespace ProjectC.Application.Tests.Orders.GetMyOrders;

public class GetMyOrdersHandlerTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly PageRequest FirstPage = new(PageRequest.DefaultPage, PageRequest.DefaultPageSize);

    private readonly FakeOrderRepository _orderRepository = new();
    private readonly FakeEventRepository _eventRepository = new();

    // [BOQ-LIST-001]
    [Fact]
    public async Task HandleAsync_WhenBuyerHasOrders_ReturnsOnlyThatBuyersOrderSummaries()
    {
        var buyerId = Guid.NewGuid();
        var firstEvent = SeedEvent("Spring Concert");
        var secondEvent = SeedEvent("Summer Festival");
        var ownOrder = SeedOrder(buyerId, firstEvent.Id);
        var ownSecondOrder = SeedOrder(buyerId, secondEvent.Id);
        var otherOrder = SeedOrder(Guid.NewGuid(), firstEvent.Id);

        var page = (await CreateHandler().HandleAsync(buyerId, FirstPage, CancellationToken.None)).Value!;

        page.Items.Should().HaveCount(2);
        page.TotalCount.Should().Be(2);
        page.Items.Should().NotContain(summary => summary.Id == otherOrder.Id);
        var summary = page.Items.Single(s => s.Id == ownOrder.Id);
        summary.EventId.Should().Be(ownOrder.EventId);
        summary.EventTitle.Should().Be("Spring Concert");
        summary.Status.Should().Be("Pending");
        summary.HeldUntilUtc.Should().Be(ownOrder.HeldUntilUtc);
        page.Items.Single(s => s.Id == ownSecondOrder.Id).EventTitle.Should().Be("Summer Festival");
    }

    // [BOQ-LIST-002 / LP-RESP-004] 0 筆訂單時不發出活動查詢（repository 的「空清單不查詢」只是額外防線）。
    [Fact]
    public async Task HandleAsync_WhenBuyerHasNoOrders_ReturnsEmptyPageWithZeroTotal()
    {
        var result = await CreateHandler().HandleAsync(Guid.NewGuid(), FirstPage, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Should().BeEmpty();
        result.Value.TotalCount.Should().Be(0);
        _eventRepository.GetByIdsCallCount.Should().Be(0);
    }

    // [BOQ-LIST-TITLE-001] 活動名稱以單次批次查詢取得，不隨訂單筆數成長。
    [Fact]
    public async Task HandleAsync_WithOrdersInTwoEvents_ReturnsEventTitlesWithSingleBatchQuery()
    {
        var buyerId = Guid.NewGuid();
        var firstEvent = SeedEvent("Spring Concert");
        var secondEvent = SeedEvent("Summer Festival");
        var firstOrder = SeedOrder(buyerId, firstEvent.Id);
        var secondOrder = SeedOrder(buyerId, secondEvent.Id);
        var thirdOrder = SeedOrder(buyerId, firstEvent.Id);

        var page = (await CreateHandler().HandleAsync(buyerId, FirstPage, CancellationToken.None)).Value!;

        page.Items.Single(s => s.Id == firstOrder.Id).EventTitle.Should().Be("Spring Concert");
        page.Items.Single(s => s.Id == secondOrder.Id).EventTitle.Should().Be("Summer Festival");
        page.Items.Single(s => s.Id == thirdOrder.Id).EventTitle.Should().Be("Spring Concert");
        _eventRepository.GetByIdsCallCount.Should().Be(1);
        // 三筆訂單只涉及兩個活動：傳入的 Id 已去重（design.md 決策 4）。
        _eventRepository.LastGetByIdsIds.Should().BeEquivalentTo([firstEvent.Id, secondEvent.Id]);
    }

    // [BOQ-LIST-PAGE-001 / LP-RESP-001] 回應帶實際套用的 page／pageSize 與不受分頁影響的總筆數；活動名稱只針對當頁查詢。
    [Fact]
    public async Task HandleAsync_WithSecondPage_ReturnsThatPageWithEnvelopeAndQueriesOnlyPageEventIds()
    {
        var buyerId = Guid.NewGuid();
        var events = Enumerable.Range(0, 5).Select(i => SeedEvent($"Event {i}")).ToList();
        // 新到舊：Event 4、3 | 2、1 | 0；pageSize 2 的第 2 頁 = Event 2、1。
        var orders = events.Select((e, i) => SeedOrder(buyerId, e.Id, Now.AddMinutes(-60 + i))).ToList();

        var page = (await CreateHandler().HandleAsync(buyerId, new PageRequest(2, 2), CancellationToken.None)).Value!;

        page.Page.Should().Be(2);
        page.PageSize.Should().Be(2);
        page.TotalCount.Should().Be(5);
        page.Items.Select(s => s.Id).Should().Equal(orders[2].Id, orders[1].Id);
        _eventRepository.LastGetByIdsIds.Should().BeEquivalentTo([events[2].Id, events[1].Id]);
        _orderRepository.LastSkip.Should().Be(2);
        _orderRepository.LastTake.Should().Be(2);
    }

    // [LP-RESP-002] 最後一頁不足一頁
    [Fact]
    public async Task HandleAsync_WithLastPageNotFull_ReturnsRemainingItems()
    {
        var buyerId = Guid.NewGuid();
        var @event = SeedEvent("Spring Concert");
        for (var i = 0; i < 5; i++)
        {
            SeedOrder(buyerId, @event.Id, Now.AddMinutes(-60 + i));
        }

        var page = (await CreateHandler().HandleAsync(buyerId, new PageRequest(3, 2), CancellationToken.None)).Value!;

        page.Items.Should().HaveCount(1);
        page.TotalCount.Should().Be(5);
    }

    // [LP-RESP-003] 頁碼超過最後一頁：成功、空 items、實際總筆數；沒有當頁資料就不查活動。
    [Fact]
    public async Task HandleAsync_WhenPageBeyondLastPage_ReturnsEmptyItemsWithActualTotal()
    {
        var buyerId = Guid.NewGuid();
        var @event = SeedEvent("Spring Concert");
        SeedOrder(buyerId, @event.Id);

        var result = await CreateHandler().HandleAsync(buyerId, new PageRequest(4, 20), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Should().BeEmpty();
        result.Value.Page.Should().Be(4);
        result.Value.TotalCount.Should().Be(1);
        _eventRepository.GetByIdsCallCount.Should().Be(0);
    }

    // [LP-VALID-001/003/005/006] 越界回 Validation（逐欄位），且 Repository 完全未被呼叫。
    [Theory]
    [InlineData(1, 101, new[] { "PageSize" })]
    [InlineData(0, 20, new[] { "Page" })]
    [InlineData(10001, 20, new[] { "Page" })]
    [InlineData(0, 101, new[] { "Page", "PageSize" })]
    public async Task HandleAsync_WhenPageRequestOutOfRange_ReturnsValidationErrorWithoutQueryingRepository(int page, int pageSize, string[] expectedFields)
    {
        var result = await CreateHandler().HandleAsync(Guid.NewGuid(), new PageRequest(page, pageSize), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        result.Error.FieldErrors!.Keys.Should().BeEquivalentTo(expectedFields);
        _orderRepository.GetPageCallCount.Should().Be(0);
        _eventRepository.GetByIdsCallCount.Should().Be(0);
    }

    // [BOQ-LIST-TITLE-002] 訊息 MUST 帶訂單 Id 與 EventId 供維運定位（design.md 決策 2）。
    [Fact]
    public async Task HandleAsync_WhenOrderEventMissing_ThrowsWithOrderAndEventIds()
    {
        var buyerId = Guid.NewGuid();
        SeedOrder(buyerId, SeedEvent("Spring Concert").Id);
        var missingEventId = Guid.NewGuid();
        var orphanOrder = SeedOrder(buyerId, missingEventId);

        var act = () => CreateHandler().HandleAsync(buyerId, FirstPage, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain(orphanOrder.Id.ToString()).And.Contain(missingEventId.ToString());
    }

    // [BOQ-LIST-TITLE-002] 資料不一致只在包含該筆的頁面失敗，另一頁正常。
    [Fact]
    public async Task HandleAsync_WhenOrphanOrderOnOtherPage_RequestedPageStillSucceeds()
    {
        var buyerId = Guid.NewGuid();
        var @event = SeedEvent("Spring Concert");
        var healthyOrder = SeedOrder(buyerId, @event.Id, Now.AddMinutes(-5));
        SeedOrder(buyerId, Guid.NewGuid(), Now.AddMinutes(-60));

        var firstPage = await CreateHandler().HandleAsync(buyerId, new PageRequest(1, 1), CancellationToken.None);
        var failingPage = () => CreateHandler().HandleAsync(buyerId, new PageRequest(2, 1), CancellationToken.None);

        firstPage.Value!.Items.Select(s => s.Id).Should().Equal(healthyOrder.Id);
        await failingPage.Should().ThrowAsync<InvalidOperationException>();
    }

    // 回傳筆數與需求相同、但缺的是其中一個 Id 時仍要判定缺失，不以筆數比較（design.md 決策 4）。
    [Fact]
    public async Task HandleAsync_WhenEventQueryReturnsSameCountButWrongId_ThrowsWithMissingEventId()
    {
        var buyerId = Guid.NewGuid();
        var firstEvent = SeedEvent("Spring Concert");
        var secondEvent = SeedEvent("Summer Festival");
        SeedOrder(buyerId, firstEvent.Id);
        var secondOrder = SeedOrder(buyerId, secondEvent.Id);
        var unrelatedEvent = new Event(Guid.NewGuid(), "Unrelated", Now.AddDays(10), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var handler = new GetMyOrdersHandler(
            _orderRepository,
            new SubstitutingEventRepository(_eventRepository, secondEvent.Id, unrelatedEvent),
            new FakeDateTimeProvider { UtcNow = Now },
            new PageRequestValidator());

        var act = () => handler.HandleAsync(buyerId, FirstPage, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain(secondOrder.Id.ToString()).And.Contain(secondEvent.Id.ToString());
    }

    [Fact]
    public async Task HandleAsync_WithCallerToken_ForwardsTokenToEveryQuery()
    {
        var buyerId = Guid.NewGuid();
        SeedOrder(buyerId, SeedEvent("Spring Concert").Id);
        using var cancellationTokenSource = new CancellationTokenSource();
        var token = cancellationTokenSource.Token;

        await CreateHandler().HandleAsync(buyerId, FirstPage, token);

        _orderRepository.LastGetPageByBuyerIdToken.Should().Be(token);
        _eventRepository.LastGetByIdsToken.Should().Be(token);
    }

    [Fact]
    public async Task HandleAsync_WhenEventQueryIsCancelled_PropagatesOperationCanceledException()
    {
        var buyerId = Guid.NewGuid();
        SeedOrder(buyerId, SeedEvent("Spring Concert").Id);
        var handler = new GetMyOrdersHandler(
            _orderRepository,
            new CancellingEventRepository(_eventRepository),
            new FakeDateTimeProvider { UtcNow = Now },
            new PageRequestValidator());

        var act = () => handler.HandleAsync(buyerId, FirstPage, CancellationToken.None);

        await act.Should().ThrowExactlyAsync<OperationCanceledException>();
    }

    private GetMyOrdersHandler CreateHandler()
        => new(_orderRepository, _eventRepository, new FakeDateTimeProvider { UtcNow = Now }, new PageRequestValidator());

    private Event SeedEvent(string title)
    {
        var @event = new Event(Guid.NewGuid(), title, Now.AddDays(30), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        _eventRepository.Data.Add(@event);
        return @event;
    }

    private Order SeedOrder(Guid buyerId, Guid eventId, DateTime? createdAtUtc = null)
    {
        var order = new Order(Guid.NewGuid(), eventId, buyerId, createdAtUtc ?? DateTime.UtcNow, Now.AddMinutes(15), [new OrderItem(Guid.NewGuid(), Guid.NewGuid(), null, 1, 500m)]);
        _orderRepository.Data.Add(order);
        return order;
    }

    // 既有 Fake 依傳入 Id 過濾，做不出「筆數相同但 Id 不同」的回傳；以 decorator 把指定 Id 換成無關的活動。
    private sealed class SubstitutingEventRepository(IEventRepository inner, Guid removedEventId, Event extraEvent) : DelegatingEventRepository(inner)
    {
        public override async Task<IReadOnlyList<Event>> GetByIdsAsync(IReadOnlyList<Guid> eventIds, CancellationToken cancellationToken)
            => (await base.GetByIdsAsync(eventIds, cancellationToken)).Where(e => e.Id != removedEventId).Append(extraEvent).ToList();
    }

    private sealed class CancellingEventRepository(IEventRepository inner) : DelegatingEventRepository(inner)
    {
        public override Task<IReadOnlyList<Event>> GetByIdsAsync(IReadOnlyList<Guid> eventIds, CancellationToken cancellationToken)
            => throw new OperationCanceledException();
    }

    private abstract class DelegatingEventRepository(IEventRepository inner) : IEventRepository
    {
        public Task<Event?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => inner.GetByIdAsync(id, cancellationToken);

        public Task<IReadOnlyList<Event>> GetAllAsync(CancellationToken cancellationToken) => inner.GetAllAsync(cancellationToken);

        public virtual Task<IReadOnlyList<Event>> GetByIdsAsync(IReadOnlyList<Guid> eventIds, CancellationToken cancellationToken)
            => inner.GetByIdsAsync(eventIds, cancellationToken);

        public Task<EventPage> GetPageByOrganizerIdAsync(Guid organizerId, int skip, int take, CancellationToken cancellationToken)
            => inner.GetPageByOrganizerIdAsync(organizerId, skip, take, cancellationToken);

        public void Add(Event @event) => inner.Add(@event);

        public void Update(Event @event) => inner.Update(@event);

        public Task<Event?> GetForUpdateAsync(Guid eventId, CancellationToken cancellationToken) => inner.GetForUpdateAsync(eventId, cancellationToken);

        public Task<Event?> GetForShareAsync(Guid eventId, CancellationToken cancellationToken) => inner.GetForShareAsync(eventId, cancellationToken);
    }
}
