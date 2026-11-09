using FluentAssertions;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Paging;
using ProjectC.Application.Events.GetEventById;
using ProjectC.Application.Events.GetEvents;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Events;

namespace ProjectC.Application.Tests.Events.GetEventById;

// event-catalog spec EVT-DETAIL-001／002／004／005／006／008（回退路徑）。
public class GetEventByIdHandlerTests
{
    // 以字面值鎖定列表快取 key：單筆查詢必須與列表共用同一份快取。
    private const string EventListCacheKeyV3 = "query-cache:events:list:v3";

    private readonly FakeEventRepository _eventRepository = new();
    private readonly FakeQueryCache _queryCache = new();
    private readonly QueryCacheOptions _options = new() { EventListTtlSeconds = 30, TicketTypesTtlSeconds = 10 };
    private readonly GetEventsHandler _getEventsHandler;
    private readonly GetEventByIdHandler _handler;

    public GetEventByIdHandlerTests()
    {
        _getEventsHandler = new GetEventsHandler(_eventRepository, _queryCache, _options, new PageRequestValidator());
        _handler = new GetEventByIdHandler(_getEventsHandler);
    }

    private static Event NewEvent(string title = "Concert") =>
        new(Guid.NewGuid(), title, DateTime.UtcNow.AddDays(1), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    // EVT-DETAIL-001
    [Fact]
    public async Task HandleAsync_WhenEventExists_ReturnsSameDtoAsListEntry()
    {
        var target = NewEvent("Target");
        _eventRepository.Data.Add(NewEvent("Other"));
        _eventRepository.Data.Add(target);

        var result = await _handler.HandleAsync(target.Id, CancellationToken.None);

        var listEntry = (await _getEventsHandler.GetAllOrderedAsync(CancellationToken.None)).Single(e => e.Id == target.Id);
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(listEntry);
        result.Value!.Title.Should().Be("Target");
    }

    // EVT-DETAIL-002
    [Fact]
    public async Task HandleAsync_WhenEventDoesNotExist_ReturnsNotFound()
    {
        _eventRepository.Data.Add(NewEvent());

        var result = await _handler.HandleAsync(Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
    }

    [Fact]
    public async Task HandleAsync_WhenIdIsEmptyGuid_ReturnsNotFound()
    {
        _eventRepository.Data.Add(NewEvent());

        var result = await _handler.HandleAsync(Guid.Empty, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
    }

    // EVT-DETAIL-004：Repository 任何方法皆不得被呼叫（殺掉「略過 GetEventsHandler 改直接 GetByIdAsync」）
    [Fact]
    public async Task HandleAsync_WhenListCacheHits_DoesNotCallAnyRepositoryMethod()
    {
        var cachedDto = new EventDto(Guid.NewGuid(), "Cached", DateTime.UtcNow.AddDays(2), Guid.NewGuid(), Guid.NewGuid(), null, null, null, false, false, null, null);
        _queryCache.Seed(EventListCacheKeyV3, (IReadOnlyList<EventDto>)[cachedDto]);

        var result = await _handler.HandleAsync(cachedDto.Id, CancellationToken.None);

        result.Value.Should().Be(cachedDto);
        _eventRepository.GetAllCallCount.Should().Be(0);
        _eventRepository.GetByIdCallCount.Should().Be(0);
        _eventRepository.GetByIdsCallCount.Should().Be(0);
        _eventRepository.GetPageCallCount.Should().Be(0);
        _queryCache.SetCalls.Should().BeEmpty();
    }

    // EVT-DETAIL-005
    [Fact]
    public async Task HandleAsync_WhenListCacheMisses_QueriesAllOnceAndWritesListCache()
    {
        var target = NewEvent();
        _eventRepository.Data.Add(target);

        var result = await _handler.HandleAsync(target.Id, CancellationToken.None);

        result.Value!.Id.Should().Be(target.Id);
        _eventRepository.GetAllCallCount.Should().Be(1);
        _eventRepository.GetByIdCallCount.Should().Be(0);
        _queryCache.SetCalls.Should().ContainSingle();
        _queryCache.SetCalls[0].Key.Should().Be(EventListCacheKeyV3);
        _queryCache.SetCalls[0].Value.Should().BeAssignableTo<IReadOnlyList<EventDto>>()
            .Subject.Should().ContainSingle(e => e.Id == target.Id);
    }

    // EVT-DETAIL-006
    [Fact]
    public async Task HandleAsync_AfterListCacheInvalidated_ReflectsLatestValue()
    {
        var target = NewEvent();
        _eventRepository.Data.Add(target);
        var before = await _handler.HandleAsync(target.Id, CancellationToken.None);
        before.Value!.IsQueueModeEnabled.Should().BeFalse();

        target.EnableQueueMode();
        await _queryCache.RemoveAsync(EventListCacheKeyV3, CancellationToken.None);
        var after = await _handler.HandleAsync(target.Id, CancellationToken.None);

        after.Value!.IsQueueModeEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WhenCacheMissesAndEventDoesNotExist_ReturnsNotFound()
    {
        var result = await _handler.HandleAsync(Guid.NewGuid(), CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.NotFound);
    }

    // EVT-LIST-006：排在列表第 2 頁之後的活動，單一活動端點仍須找得到
    [Fact]
    public async Task HandleAsync_WhenEventIsBeyondFirstListPage_ReturnsSuccess()
    {
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 1; i <= 24; i++)
        {
            _eventRepository.Data.Add(new Event(Guid.NewGuid(), $"Newer {i}", DateTime.UtcNow.AddDays(1), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), createdAtUtc: baseTime.AddDays(i)));
        }

        var oldest = new Event(Guid.NewGuid(), "Oldest", DateTime.UtcNow.AddDays(1), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), createdAtUtc: baseTime);
        _eventRepository.Data.Add(oldest);

        var result = await _handler.HandleAsync(oldest.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue("25 筆時最舊的活動排在第 2 頁，不得因列表分頁而查不到");
        result.Value!.Title.Should().Be("Oldest");
    }

    [Fact]
    public async Task HandleAsync_PassesCancellationTokenToRepository()
    {
        var target = NewEvent();
        _eventRepository.Data.Add(target);
        using var cts = new CancellationTokenSource();

        await _handler.HandleAsync(target.Id, cts.Token);

        _eventRepository.LastGetAllToken.Should().Be(cts.Token);
    }
}
