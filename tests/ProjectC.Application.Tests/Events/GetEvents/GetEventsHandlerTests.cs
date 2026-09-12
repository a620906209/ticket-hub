using FluentAssertions;
using ProjectC.Application.Common;
using ProjectC.Application.Events.GetEvents;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Events;

namespace ProjectC.Application.Tests.Events.GetEvents;

// query-caching spec QC-EVT-001／002／003（tasks.md 2.2～2.5）。
public class GetEventsHandlerTests
{
    private readonly FakeEventRepository _eventRepository = new();
    private readonly FakeQueryCache _queryCache = new();
    private readonly QueryCacheOptions _options = new() { EventListTtlSeconds = 30, TicketTypesTtlSeconds = 10 };
    private readonly GetEventsHandler _handler;

    public GetEventsHandlerTests()
    {
        _handler = new GetEventsHandler(_eventRepository, _queryCache, _options);
    }

    private static Event NewEvent() => new(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(1), Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public async Task HandleAsync_WhenCacheMisses_QueriesDatabaseAndWritesResultToCacheWithConfiguredTtl()
    {
        var @event = NewEvent();
        _eventRepository.Data.Add(@event);

        var result = await _handler.HandleAsync(CancellationToken.None);

        result.Should().ContainSingle(e => e.Id == @event.Id);
        _queryCache.SetCalls.Should().ContainSingle();
        var setCall = _queryCache.SetCalls[0];
        setCall.Key.Should().Be(GetEventsHandler.CacheKey);
        setCall.Ttl.Should().Be(TimeSpan.FromSeconds(_options.EventListTtlSeconds));
        var cachedValue = setCall.Value.Should().BeAssignableTo<IReadOnlyList<EventDto>>().Subject;
        cachedValue.Should().ContainSingle(e => e.Id == @event.Id);
    }

    [Fact]
    public async Task HandleAsync_WhenCacheHits_ReturnsCachedResultWithoutQueryingDatabase()
    {
        var cachedDto = new EventDto(Guid.NewGuid(), "Cached Concert", DateTime.UtcNow.AddDays(2), Guid.NewGuid(), Guid.NewGuid(), null, null, null, false);
        _queryCache.Seed(GetEventsHandler.CacheKey, (IReadOnlyList<EventDto>)[cachedDto]);

        var result = await _handler.HandleAsync(CancellationToken.None);

        result.Should().ContainSingle(e => e.Id == cachedDto.Id);
        _eventRepository.Data.Should().BeEmpty("這個測試沒有放任何資料到資料庫，若 Handler 真的查了 DB 也不會拿到這筆快取內容");
        _queryCache.SetCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_CacheMissThenHit_ReturnsIdenticalEventDtoListBothTimes()
    {
        var @event = NewEvent();
        _eventRepository.Data.Add(@event);

        var missResult = await _handler.HandleAsync(CancellationToken.None);
        var hitResult = await _handler.HandleAsync(CancellationToken.None);

        hitResult.Should().BeEquivalentTo(missResult, options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task HandleAsync_WhenNoEventsExist_ReturnsEmptyListAndStillWritesEmptyListToCache()
    {
        var result = await _handler.HandleAsync(CancellationToken.None);

        result.Should().BeEmpty();
        _queryCache.SetCalls.Should().ContainSingle();
        _queryCache.SetCalls[0].Value.Should().BeAssignableTo<IReadOnlyList<EventDto>>().Subject.Should().BeEmpty();
    }
}
