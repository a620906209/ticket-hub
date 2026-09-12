using FluentAssertions;
using ProjectC.Application.Common;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Application.Tickets.GetTicketTypes;
using ProjectC.Domain.Events;
using ProjectC.Domain.Venues;

namespace ProjectC.Application.Tests.Tickets.GetTicketTypes;

public class GetTicketTypesHandlerTests
{
    private readonly FakeEventRepository _eventRepository = new();
    private readonly FakeTicketTypeRepository _ticketTypeRepository = new();
    private readonly FakeQueryCache _queryCache = new();
    private readonly GetTicketTypesHandler _handler;

    public GetTicketTypesHandlerTests()
    {
        _handler = new GetTicketTypesHandler(_eventRepository, _ticketTypeRepository, _queryCache, new QueryCacheOptions { EventListTtlSeconds = 30, TicketTypesTtlSeconds = 10 });
    }

    [Fact]
    public async Task HandleAsync_ReturnsRequiresSeatAndAvailableQuantityForBothModes()
    {
        var seatMap = new SeatMap(Guid.NewGuid(), Guid.NewGuid());
        seatMap.AddSeat("A", "1");
        var @event = new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(1), Guid.NewGuid(), seatMap.Id);
        _eventRepository.Data.Add(@event);

        var seatTicketType = @event.CreateTicketType("A", 500m, seatMap);
        var countTicketType = @event.CreateCountBasedTicketType("站票", 300m, 50);
        _ticketTypeRepository.Data.Add(seatTicketType);
        _ticketTypeRepository.Data.Add(countTicketType);

        var result = await _handler.HandleAsync(@event.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle(t => t.Id == seatTicketType.Id && t.RequiresSeat && t.AvailableQuantity == null);
        result.Value.Should().ContainSingle(t => t.Id == countTicketType.Id && !t.RequiresSeat && t.AvailableQuantity == 50);
    }

    private Event AddEvent()
    {
        var @event = new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(1), Guid.NewGuid(), Guid.NewGuid());
        _eventRepository.Data.Add(@event);
        return @event;
    }

    // QC-TT-001（tasks.md 3.2）。
    [Fact]
    public async Task HandleAsync_WhenCacheMisses_QueriesDatabaseAndWritesResultToCacheWithConfiguredTtl()
    {
        var @event = AddEvent();
        var ticketType = @event.CreateCountBasedTicketType("站票", 300m, 50);
        _ticketTypeRepository.Data.Add(ticketType);

        var result = await _handler.HandleAsync(@event.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _queryCache.SetCalls.Should().ContainSingle();
        var setCall = _queryCache.SetCalls[0];
        setCall.Key.Should().Be(GetTicketTypesHandler.BuildCacheKey(@event.Id));
        setCall.Ttl.Should().Be(TimeSpan.FromSeconds(10));
    }

    // QC-TT-002（tasks.md 3.3）。
    [Fact]
    public async Task HandleAsync_WhenCacheHits_ReturnsCachedResultWithoutQueryingDatabase()
    {
        var @event = AddEvent();
        var cachedDto = new TicketTypeDto(Guid.NewGuid(), "站票", 300m, false, 50);
        _queryCache.Seed(GetTicketTypesHandler.BuildCacheKey(@event.Id), (IReadOnlyList<TicketTypeDto>)[cachedDto]);

        var result = await _handler.HandleAsync(@event.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle(t => t.Id == cachedDto.Id);
        _ticketTypeRepository.Data.Should().BeEmpty("這個測試沒有放任何票種到資料庫，若 Handler 真的查了 DB 也不會拿到這筆快取內容");
    }

    // QC-TT-003（tasks.md 3.4）：不同活動的快取 key 互不干擾。
    [Fact]
    public async Task HandleAsync_ForDifferentEvents_UsesIndependentCacheKeysAndDoesNotCrossContaminate()
    {
        var eventA = AddEvent();
        var eventB = AddEvent();
        var cachedDtoForA = new TicketTypeDto(Guid.NewGuid(), "A區", 500m, false, 10);
        _queryCache.Seed(GetTicketTypesHandler.BuildCacheKey(eventA.Id), (IReadOnlyList<TicketTypeDto>)[cachedDtoForA]);
        var ticketTypeForB = eventB.CreateCountBasedTicketType("B區", 300m, 20);
        _ticketTypeRepository.Data.Add(ticketTypeForB);

        await _handler.HandleAsync(eventA.Id, CancellationToken.None);
        var resultB = await _handler.HandleAsync(eventB.Id, CancellationToken.None);

        resultB.IsSuccess.Should().BeTrue();
        resultB.Value.Should().ContainSingle(t => t.Id == ticketTypeForB.Id);
        _queryCache.SetCalls.Should().ContainSingle(c => c.Key == GetTicketTypesHandler.BuildCacheKey(eventB.Id));
    }

    // QC-TT-004（tasks.md 3.5）：活動不存在時，存在性檢查須在快取查詢之前完成。
    [Fact]
    public async Task HandleAsync_WhenEventDoesNotExist_ReturnsNotFoundWithoutTouchingCache()
    {
        var result = await _handler.HandleAsync(Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
        _queryCache.GetCalls.Should().BeEmpty();
        _queryCache.SetCalls.Should().BeEmpty();
    }

    // QC-TT-005（tasks.md 3.6）。
    [Fact]
    public async Task HandleAsync_WhenEventHasNoTicketTypes_ReturnsEmptyListAndStillWritesEmptyListToCache()
    {
        var @event = AddEvent();

        var result = await _handler.HandleAsync(@event.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
        _queryCache.SetCalls.Should().ContainSingle();
        _queryCache.SetCalls[0].Value.Should().BeAssignableTo<IReadOnlyList<TicketTypeDto>>().Subject.Should().BeEmpty();
    }
}
