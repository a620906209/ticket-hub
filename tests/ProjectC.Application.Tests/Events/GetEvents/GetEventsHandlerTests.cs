using FluentAssertions;
using ProjectC.Application.Common;
using ProjectC.Application.Events.GetEvents;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Events;

namespace ProjectC.Application.Tests.Events.GetEvents;

// query-caching spec QC-EVT-001／002／003（tasks.md 2.2～2.5）。
public class GetEventsHandlerTests
{
    // 以字面值鎖定 v2 契約，不引用 GetEventsHandler.CacheKey：常數被改回舊值時測試必須失敗（event-sales-window tasks 5.5）。
    private const string EventListCacheKeyV2 = "query-cache:events:list:v2";
    private const string LegacyEventListCacheKey = "query-cache:events:list";

    private readonly FakeEventRepository _eventRepository = new();
    private readonly FakeQueryCache _queryCache = new();
    private readonly QueryCacheOptions _options = new() { EventListTtlSeconds = 30, TicketTypesTtlSeconds = 10 };
    private readonly GetEventsHandler _handler;

    public GetEventsHandlerTests()
    {
        _handler = new GetEventsHandler(_eventRepository, _queryCache, _options);
    }

    private static Event NewEvent() => new(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(1), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    // QC-EVT-001；QC-TTL-001：單元面
    [Fact]
    public async Task HandleAsync_WhenCacheMisses_QueriesDatabaseAndWritesResultToCacheWithConfiguredTtl()
    {
        var @event = NewEvent();
        _eventRepository.Data.Add(@event);

        var result = await _handler.HandleAsync(CancellationToken.None);

        result.Should().ContainSingle(e => e.Id == @event.Id);
        _eventRepository.GetAllCallCount.Should().Be(1);
        _queryCache.SetCalls.Should().ContainSingle();
        var setCall = _queryCache.SetCalls[0];
        setCall.Key.Should().Be(EventListCacheKeyV2);
        setCall.Ttl.Should().Be(TimeSpan.FromSeconds(_options.EventListTtlSeconds));
        var cachedValue = setCall.Value.Should().BeAssignableTo<IReadOnlyList<EventDto>>().Subject;
        cachedValue.Should().ContainSingle(e => e.Id == @event.Id);
        _queryCache.GetCalls.Should().NotContain(LegacyEventListCacheKey);
        _queryCache.SetCalls.Should().NotContain(call => call.Key == LegacyEventListCacheKey);
    }

    // QC-EVT-002：單元面
    [Fact]
    public async Task HandleAsync_WhenCacheHits_ReturnsCachedResultWithoutQueryingDatabase()
    {
        var cachedDto = new EventDto(Guid.NewGuid(), "Cached Concert", DateTime.UtcNow.AddDays(2), Guid.NewGuid(), Guid.NewGuid(), null, null, null, false, false, null, null);
        _queryCache.Seed(EventListCacheKeyV2, (IReadOnlyList<EventDto>)[cachedDto]);

        var result = await _handler.HandleAsync(CancellationToken.None);

        result.Should().ContainSingle(e => e.Id == cachedDto.Id);
        _eventRepository.GetAllCallCount.Should().Be(0);
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

    // QC-EVT-003
    [Fact]
    public async Task HandleAsync_WhenNoEventsExist_ReturnsEmptyListAndStillWritesEmptyListToCache()
    {
        var first = await _handler.HandleAsync(CancellationToken.None);
        var second = await _handler.HandleAsync(CancellationToken.None);

        first.Should().BeEmpty();
        second.Should().BeEmpty();
        _eventRepository.GetAllCallCount.Should().Be(1, "第二次應命中快取的空列表，不再查詢資料庫");
        _queryCache.SetCalls.Should().ContainSingle("第二次命中快取，不再寫入");
        _queryCache.SetCalls[0].Key.Should().Be(EventListCacheKeyV2);
        _queryCache.SetCalls[0].Value.Should().BeAssignableTo<IReadOnlyList<EventDto>>().Subject.Should().BeEmpty();
    }

    // TP-BROWSE-SALES-001：Application 面，DTO 帶原始值（null 不展開成 StartAtUtc）
    [Fact]
    public async Task HandleAsync_WithAndWithoutSalesWindow_MapsRawSalesWindowToDto()
    {
        var startAt = DateTime.UtcNow.AddDays(10);
        var salesStart = startAt.AddDays(-9);
        var salesEnd = startAt.AddDays(-1);
        var withWindow = new Event(Guid.NewGuid(), "With Window", startAt, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            salesStartAtUtc: salesStart, salesEndAtUtc: salesEnd);
        var withoutWindow = NewEvent();
        _eventRepository.Data.Add(withWindow);
        _eventRepository.Data.Add(withoutWindow);

        var result = await _handler.HandleAsync(CancellationToken.None);

        var withWindowDto = result.Single(e => e.Id == withWindow.Id);
        withWindowDto.SalesStartAtUtc.Should().Be(salesStart);
        withWindowDto.SalesEndAtUtc.Should().Be(salesEnd);
        var withoutWindowDto = result.Single(e => e.Id == withoutWindow.Id);
        withoutWindowDto.SalesStartAtUtc.Should().BeNull();
        withoutWindowDto.SalesEndAtUtc.Should().BeNull();
    }
}
