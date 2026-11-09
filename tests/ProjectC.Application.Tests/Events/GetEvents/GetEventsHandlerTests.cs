using FluentAssertions;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Paging;
using ProjectC.Application.Events.GetEvents;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Events;

namespace ProjectC.Application.Tests.Events.GetEvents;

// query-caching spec QC-EVT-001／002／003、QC-EVT-VER-005；event-catalog spec EVT-LIST-001～005、EVT-LIST-ORDER-001～004。
public class GetEventsHandlerTests
{
    // 以字面值鎖定 v3 契約，不引用 GetEventsHandler.CacheKey：常數被改回舊值時測試必須失敗（event-sales-window tasks 5.5）。
    private const string EventListCacheKeyV3 = "query-cache:events:list:v3";
    private const string LegacyEventListCacheKey = "query-cache:events:list";
    private const string LegacyEventListCacheKeyV2 = "query-cache:events:list:v2";

    private readonly FakeEventRepository _eventRepository = new();
    private readonly FakeQueryCache _queryCache = new();
    private readonly QueryCacheOptions _options = new() { EventListTtlSeconds = 30, TicketTypesTtlSeconds = 10 };
    private readonly GetEventsHandler _handler;

    public GetEventsHandlerTests()
    {
        _handler = new GetEventsHandler(_eventRepository, _queryCache, _options, new PageRequestValidator());
    }

    private static Event NewEvent(DateTime? createdAtUtc = null, Guid? id = null, string title = "Concert") =>
        new(id ?? Guid.NewGuid(), title, DateTime.UtcNow.AddDays(1), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), createdAtUtc: createdAtUtc);

    private static readonly DateTime BaseTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private void AddEventsWithDistinctCreatedAt(int count)
    {
        for (var i = 0; i < count; i++)
        {
            _eventRepository.Data.Add(NewEvent(BaseTime.AddMinutes(i)));
        }
    }

    private async Task<PagedResultDto<EventDto>> GetPageAsync(int page, int pageSize)
    {
        var result = await _handler.HandleAsync(new PageRequest(page, pageSize), CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        return result.Value!;
    }

    // EVT-LIST-ORDER-001
    [Fact]
    public async Task HandleAsync_WhenCreatedAtDiffers_OrdersNewestFirst()
    {
        var oldest = NewEvent(BaseTime);
        var newest = NewEvent(BaseTime.AddDays(2));
        var middle = NewEvent(BaseTime.AddDays(1));
        _eventRepository.Data.AddRange([oldest, newest, middle]);

        var page = await GetPageAsync(1, 20);

        page.Items.Select(e => e.Id).Should().Equal(newest.Id, middle.Id, oldest.Id);
    }

    // EVT-LIST-ORDER-002
    [Fact]
    public async Task HandleAsync_WhenCreatedAtIsNull_PlacesThoseEventsLast()
    {
        var legacy = NewEvent(null);
        var recent = NewEvent(BaseTime);
        var older = NewEvent(BaseTime.AddDays(-30));
        _eventRepository.Data.AddRange([legacy, older, recent]);

        var page = await GetPageAsync(1, 20);

        page.Items.Select(e => e.Id).Should().Equal(recent.Id, older.Id, legacy.Id);
    }

    // EVT-LIST-ORDER-003
    [Fact]
    public async Task HandleAsync_WhenCreatedAtTies_OrdersByIdAscending()
    {
        var sameTime = new[] { NewEvent(BaseTime), NewEvent(BaseTime), NewEvent(BaseTime) };
        var bothNull = new[] { NewEvent(null), NewEvent(null), NewEvent(null) };
        _eventRepository.Data.AddRange(bothNull.Concat(sameTime));

        var page = await GetPageAsync(1, 20);

        var expected = sameTime.OrderBy(e => e.Id).Concat(bothNull.OrderBy(e => e.Id)).Select(e => e.Id);
        page.Items.Select(e => e.Id).Should().Equal(expected);
    }

    // EVT-LIST-001
    [Fact]
    public async Task HandleAsync_WithDefaultPaging_ReturnsFirstTwentyAndTotalCount()
    {
        AddEventsWithDistinctCreatedAt(45);

        var page = await GetPageAsync(PageRequest.DefaultPage, PageRequest.DefaultPageSize);

        page.Page.Should().Be(1);
        page.PageSize.Should().Be(20);
        page.TotalCount.Should().Be(45);
        page.Items.Should().HaveCount(20);
    }

    // EVT-LIST-002
    [Fact]
    public async Task HandleAsync_OnLastPartialPage_ReturnsRemainingItems()
    {
        AddEventsWithDistinctCreatedAt(45);

        var page = await GetPageAsync(3, 20);

        page.Items.Should().HaveCount(5);
        page.TotalCount.Should().Be(45);
        var allOrdered = (await GetPageAsync(1, 100)).Items;
        page.Items.Select(e => e.Id).Should().Equal(allOrdered.Skip(40).Select(e => e.Id));
    }

    // EVT-LIST-003
    [Fact]
    public async Task HandleAsync_WhenPageBeyondLastPage_ReturnsEmptyItemsWithUnchangedTotalCount()
    {
        AddEventsWithDistinctCreatedAt(45);

        var page = await GetPageAsync(4, 20);

        page.Items.Should().BeEmpty();
        page.TotalCount.Should().Be(45);
        page.Page.Should().Be(4);
    }

    // EVT-LIST-004
    [Fact]
    public async Task HandleAsync_WhenNoEventsExist_ReturnsEmptyPageWithZeroTotalCount()
    {
        var page = await GetPageAsync(1, 20);

        page.Items.Should().BeEmpty();
        page.TotalCount.Should().Be(0);
    }

    // EVT-LIST-ORDER-004
    [Fact]
    public async Task HandleAsync_WalkingAllPages_ReturnsEveryEventExactlyOnce()
    {
        AddEventsWithDistinctCreatedAt(11);
        _eventRepository.Data.Add(NewEvent(null));
        _eventRepository.Data.Add(NewEvent(BaseTime));

        var collected = new List<Guid>();
        for (var page = 1; page <= 5; page++)
        {
            collected.AddRange((await GetPageAsync(page, 3)).Items.Select(e => e.Id));
        }

        collected.Should().HaveCount(13);
        collected.Should().OnlyHaveUniqueItems();
        collected.Should().BeEquivalentTo(_eventRepository.Data.Select(e => e.Id));
    }

    // EVT-LIST-005（handler 層）：驗證須在碰快取與 Repository 之前
    [Theory]
    [InlineData(0, 20, "Page")]
    [InlineData(10001, 20, "Page")]
    [InlineData(1, 0, "PageSize")]
    [InlineData(1, 101, "PageSize")]
    public async Task HandleAsync_WhenPagingOutOfRange_ReturnsValidationErrorWithoutTouchingCacheOrRepository(int page, int pageSize, string expectedField)
    {
        AddEventsWithDistinctCreatedAt(3);

        var result = await _handler.HandleAsync(new PageRequest(page, pageSize), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        result.Error.FieldErrors!.Keys.Should().BeEquivalentTo([expectedField]);
        _eventRepository.GetAllCallCount.Should().Be(0);
        _queryCache.GetCalls.Should().BeEmpty();
        _queryCache.SetCalls.Should().BeEmpty();
    }

    // QC-EVT-001；QC-TTL-001；QC-EVT-VER-005：單元面
    [Fact]
    public async Task HandleAsync_WhenCacheMisses_WritesWholeOrderedListToV3KeyWithConfiguredTtl()
    {
        AddEventsWithDistinctCreatedAt(12);
        _eventRepository.Data.Add(NewEvent(null));

        var page = await GetPageAsync(2, 5);

        page.Items.Should().HaveCount(5);
        _eventRepository.GetAllCallCount.Should().Be(1);
        _queryCache.SetCalls.Should().ContainSingle();
        var setCall = _queryCache.SetCalls[0];
        setCall.Key.Should().Be(EventListCacheKeyV3);
        setCall.Ttl.Should().Be(TimeSpan.FromSeconds(_options.EventListTtlSeconds));
        var cachedValue = setCall.Value.Should().BeAssignableTo<IReadOnlyList<EventDto>>().Subject;
        cachedValue.Should().HaveCount(13, "快取存整份列表，不是請求的那一頁");
        var expectedOrder = _eventRepository.Data
            .OrderBy(e => e.CreatedAtUtc == null).ThenByDescending(e => e.CreatedAtUtc).ThenBy(e => e.Id)
            .Select(e => e.Id);
        cachedValue.Select(e => e.Id).Should().Equal(expectedOrder);
        _queryCache.GetCalls.Should().NotContain(LegacyEventListCacheKey);
        _queryCache.GetCalls.Should().NotContain(LegacyEventListCacheKeyV2);
        _queryCache.SetCalls.Should().NotContain(call => call.Key == LegacyEventListCacheKey || call.Key == LegacyEventListCacheKeyV2);
    }

    // QC-EVT-002：單元面
    [Fact]
    public async Task HandleAsync_WhenCacheHits_ServesSliceFromCacheWithoutQueryingDatabase()
    {
        var cached = Enumerable.Range(0, 7)
            .Select(i => new EventDto(Guid.NewGuid(), $"Cached {i}", DateTime.UtcNow.AddDays(2), Guid.NewGuid(), Guid.NewGuid(), null, null, null, false, false, null, null))
            .ToList();
        _queryCache.Seed(EventListCacheKeyV3, (IReadOnlyList<EventDto>)cached);

        var page = await GetPageAsync(2, 3);

        page.Items.Select(e => e.Id).Should().Equal(cached.Skip(3).Take(3).Select(e => e.Id));
        page.TotalCount.Should().Be(7);
        _eventRepository.GetAllCallCount.Should().Be(0);
        _queryCache.SetCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_CacheMissThenHit_ReturnsIdenticalPageBothTimes()
    {
        AddEventsWithDistinctCreatedAt(8);
        _eventRepository.Data.Add(NewEvent(null));

        var missResult = await GetPageAsync(2, 4);
        var hitResult = await GetPageAsync(2, 4);

        _eventRepository.GetAllCallCount.Should().Be(1);
        hitResult.Should().BeEquivalentTo(missResult, options => options.WithStrictOrdering());
    }

    // QC-EVT-003
    [Fact]
    public async Task HandleAsync_WhenNoEventsExist_StillWritesEmptyListToCache()
    {
        await GetPageAsync(1, 20);
        await GetPageAsync(1, 20);

        _eventRepository.GetAllCallCount.Should().Be(1, "第二次應命中快取的空列表，不再查詢資料庫");
        _queryCache.SetCalls.Should().ContainSingle("第二次命中快取，不再寫入");
        _queryCache.SetCalls[0].Key.Should().Be(EventListCacheKeyV3);
        _queryCache.SetCalls[0].Value.Should().BeAssignableTo<IReadOnlyList<EventDto>>().Subject.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAllOrderedAsync_WhenCacheMisses_ReturnsWholeOrderedListAndCachesIt()
    {
        var older = NewEvent(BaseTime);
        var newer = NewEvent(BaseTime.AddHours(1));
        var legacy = NewEvent(null);
        _eventRepository.Data.AddRange([legacy, older, newer]);

        var all = await _handler.GetAllOrderedAsync(CancellationToken.None);

        all.Select(e => e.Id).Should().Equal(newer.Id, older.Id, legacy.Id);
        _queryCache.SetCalls.Should().ContainSingle().Which.Key.Should().Be(EventListCacheKeyV3);
    }

    [Fact]
    public async Task GetAllOrderedAsync_WhenCacheHits_ReturnsCachedListWithoutQueryingDatabase()
    {
        var cachedDto = new EventDto(Guid.NewGuid(), "Cached", DateTime.UtcNow.AddDays(2), Guid.NewGuid(), Guid.NewGuid(), null, null, null, false, false, null, null);
        _queryCache.Seed(EventListCacheKeyV3, (IReadOnlyList<EventDto>)[cachedDto]);

        var all = await _handler.GetAllOrderedAsync(CancellationToken.None);

        all.Should().ContainSingle(e => e.Id == cachedDto.Id);
        _eventRepository.GetAllCallCount.Should().Be(0);
    }

    // hardener：CancellationToken 原樣傳到快取讀、快取寫與 Repository
    [Fact]
    public async Task HandleAsync_PassesCancellationTokenToCacheAndRepository()
    {
        AddEventsWithDistinctCreatedAt(2);
        using var cts = new CancellationTokenSource();

        await _handler.HandleAsync(new PageRequest(1, 20), cts.Token);

        _queryCache.LastGetToken.Should().Be(cts.Token);
        _queryCache.LastSetToken.Should().Be(cts.Token);
        _eventRepository.LastGetAllToken.Should().Be(cts.Token);
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

        var result = (await GetPageAsync(1, 20)).Items;

        var withWindowDto = result.Single(e => e.Id == withWindow.Id);
        withWindowDto.SalesStartAtUtc.Should().Be(salesStart);
        withWindowDto.SalesEndAtUtc.Should().Be(salesEnd);
        var withoutWindowDto = result.Single(e => e.Id == withoutWindow.Id);
        withoutWindowDto.SalesStartAtUtc.Should().BeNull();
        withoutWindowDto.SalesEndAtUtc.Should().BeNull();
    }
}
