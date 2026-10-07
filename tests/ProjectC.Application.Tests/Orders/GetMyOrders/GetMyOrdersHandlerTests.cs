using FluentAssertions;
using ProjectC.Application.Orders.GetMyOrders;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Events;
using ProjectC.Domain.Orders;

namespace ProjectC.Application.Tests.Orders.GetMyOrders;

public class GetMyOrdersHandlerTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

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

        var result = await CreateHandler().HandleAsync(buyerId, CancellationToken.None);

        result.Should().HaveCount(2);
        result.Should().NotContain(summary => summary.Id == otherOrder.Id);
        var summary = result.Single(s => s.Id == ownOrder.Id);
        summary.EventId.Should().Be(ownOrder.EventId);
        summary.EventTitle.Should().Be("Spring Concert");
        summary.Status.Should().Be("Pending");
        summary.HeldUntilUtc.Should().Be(ownOrder.HeldUntilUtc);
        result.Single(s => s.Id == ownSecondOrder.Id).EventTitle.Should().Be("Summer Festival");
    }

    // [BOQ-LIST-002] 0 筆訂單時不發出活動查詢（repository 的「空清單不查詢」只是額外防線）。
    [Fact]
    public async Task HandleAsync_WhenBuyerHasNoOrders_ReturnsEmptyList()
    {
        var result = await CreateHandler().HandleAsync(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeEmpty();
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

        var result = await CreateHandler().HandleAsync(buyerId, CancellationToken.None);

        result.Single(s => s.Id == firstOrder.Id).EventTitle.Should().Be("Spring Concert");
        result.Single(s => s.Id == secondOrder.Id).EventTitle.Should().Be("Summer Festival");
        result.Single(s => s.Id == thirdOrder.Id).EventTitle.Should().Be("Spring Concert");
        _eventRepository.GetByIdsCallCount.Should().Be(1);
        // 三筆訂單只涉及兩個活動：傳入的 Id 已去重（design.md 決策 4）。
        _eventRepository.LastGetByIdsIds.Should().BeEquivalentTo([firstEvent.Id, secondEvent.Id]);
    }

    // [BOQ-LIST-TITLE-002] 訊息 MUST 帶訂單 Id 與 EventId 供維運定位（design.md 決策 2）。
    [Fact]
    public async Task HandleAsync_WhenOrderEventMissing_ThrowsWithOrderAndEventIds()
    {
        var buyerId = Guid.NewGuid();
        SeedOrder(buyerId, SeedEvent("Spring Concert").Id);
        var missingEventId = Guid.NewGuid();
        var orphanOrder = SeedOrder(buyerId, missingEventId);

        var act = () => CreateHandler().HandleAsync(buyerId, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain(orphanOrder.Id.ToString()).And.Contain(missingEventId.ToString());
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
            new FakeDateTimeProvider { UtcNow = Now });

        var act = () => handler.HandleAsync(buyerId, CancellationToken.None);

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

        await CreateHandler().HandleAsync(buyerId, token);

        _orderRepository.LastGetByBuyerIdToken.Should().Be(token);
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
            new FakeDateTimeProvider { UtcNow = Now });

        var act = () => handler.HandleAsync(buyerId, CancellationToken.None);

        await act.Should().ThrowExactlyAsync<OperationCanceledException>();
    }

    private GetMyOrdersHandler CreateHandler()
        => new(_orderRepository, _eventRepository, new FakeDateTimeProvider { UtcNow = Now });

    private Event SeedEvent(string title)
    {
        var @event = new Event(Guid.NewGuid(), title, Now.AddDays(30), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        _eventRepository.Data.Add(@event);
        return @event;
    }

    private Order SeedOrder(Guid buyerId, Guid eventId)
    {
        var order = new Order(Guid.NewGuid(), eventId, buyerId, Now.AddMinutes(15), [new OrderItem(Guid.NewGuid(), Guid.NewGuid(), null, 1, 500m)]);
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

        public Task<IReadOnlyList<Event>> GetByOrganizerIdAsync(Guid organizerId, CancellationToken cancellationToken)
            => inner.GetByOrganizerIdAsync(organizerId, cancellationToken);

        public void Add(Event @event) => inner.Add(@event);

        public void Update(Event @event) => inner.Update(@event);

        public Task<Event?> GetForUpdateAsync(Guid eventId, CancellationToken cancellationToken) => inner.GetForUpdateAsync(eventId, cancellationToken);

        public Task<Event?> GetForShareAsync(Guid eventId, CancellationToken cancellationToken) => inner.GetForShareAsync(eventId, cancellationToken);
    }
}
