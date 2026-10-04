using FluentAssertions;
using ProjectC.Application.Common;
using ProjectC.Application.Events.CreateEvent;
using ProjectC.Application.Events.GetEvents;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Venues;

namespace ProjectC.Application.Tests.Events.CreateEvent;

public class CreateEventHandlerTests
{
    private readonly FakeVenueRepository _venueRepository = new();
    private readonly FakeSeatMapRepository _seatMapRepository = new();
    private readonly FakeEventRepository _eventRepository = new();
    private readonly FakeEventSeatRepository _eventSeatRepository = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly FakeDateTimeProvider _dateTimeProvider = new();
    private readonly FakeQueryCache _queryCache = new();
    private readonly CreateEventHandler _handler;
    private static readonly Guid AdminMemberId = Guid.NewGuid();
    private static readonly Guid OrganizerId = Guid.NewGuid();

    public CreateEventHandlerTests()
    {
        _handler = new CreateEventHandler(
            _venueRepository, _seatMapRepository, _eventRepository, _eventSeatRepository, _unitOfWork,
            new CreateEventRequestValidator(), _dateTimeProvider, _queryCache);
    }

    private (Guid VenueId, Guid SeatMapId) SeedVenueAndSeatMap(int seatCount)
    {
        var venue = new Venue(Guid.NewGuid(), "Test Venue");
        _venueRepository.Data.Add(venue);

        var seatMap = new SeatMap(Guid.NewGuid(), venue.Id);
        for (var i = 0; i < seatCount; i++)
        {
            seatMap.AddSeat("A", $"{i + 1}");
        }
        _seatMapRepository.Data.Add(seatMap);

        return (venue.Id, seatMap.Id);
    }

    [Fact]
    public async Task HandleAsync_WithValidVenueAndSeatMap_CreatesEventAndEventSeats()
    {
        var (venueId, seatMapId) = SeedVenueAndSeatMap(seatCount: 3);
        var request = new CreateEventRequest("Concert", DateTime.UtcNow.AddDays(30), venueId, seatMapId);

        var result = await _handler.HandleAsync(AdminMemberId, OrganizerId, request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _eventRepository.Data.Should().ContainSingle(e => e.Id == result.Value);
        _eventSeatRepository.Data.Should().HaveCount(3);
        _eventSeatRepository.Data.Should().OnlyContain(es => es.EventId == result.Value);
    }

    [Fact]
    public async Task HandleAsync_WithBlankTitle_ReturnsValidationError()
    {
        var (venueId, seatMapId) = SeedVenueAndSeatMap(seatCount: 1);
        var request = new CreateEventRequest("  ", DateTime.UtcNow.AddDays(30), venueId, seatMapId);

        var result = await _handler.HandleAsync(AdminMemberId, OrganizerId, request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        _eventRepository.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WithDefaultStartAtUtc_ReturnsValidationError()
    {
        var (venueId, seatMapId) = SeedVenueAndSeatMap(seatCount: 1);
        var request = new CreateEventRequest("Concert", default, venueId, seatMapId);

        var result = await _handler.HandleAsync(AdminMemberId, OrganizerId, request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        _eventRepository.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WithZeroMaxTicketsPerOrder_ReturnsValidationError()
    {
        var (venueId, seatMapId) = SeedVenueAndSeatMap(seatCount: 1);
        var request = new CreateEventRequest("Concert", DateTime.UtcNow.AddDays(30), venueId, seatMapId, MaxTicketsPerOrder: 0);

        var result = await _handler.HandleAsync(AdminMemberId, OrganizerId, request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        _eventRepository.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WithPositiveMaxTicketsPerOrder_CreatesEventWithLimit()
    {
        var (venueId, seatMapId) = SeedVenueAndSeatMap(seatCount: 1);
        var request = new CreateEventRequest("Concert", DateTime.UtcNow.AddDays(30), venueId, seatMapId, MaxTicketsPerOrder: 2);

        var result = await _handler.HandleAsync(AdminMemberId, OrganizerId, request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _eventRepository.Data.Single(e => e.Id == result.Value).MaxTicketsPerOrder.Should().Be(2);
    }

    [Fact]
    public async Task HandleAsync_WithNonExistentVenue_ReturnsNotFound()
    {
        var (_, seatMapId) = SeedVenueAndSeatMap(seatCount: 1);
        var request = new CreateEventRequest("Concert", DateTime.UtcNow.AddDays(30), Guid.NewGuid(), seatMapId);

        var result = await _handler.HandleAsync(AdminMemberId, OrganizerId, request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
        _eventRepository.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WithNonExistentSeatMap_ReturnsNotFound()
    {
        var (venueId, _) = SeedVenueAndSeatMap(seatCount: 1);
        var request = new CreateEventRequest("Concert", DateTime.UtcNow.AddDays(30), venueId, Guid.NewGuid());

        var result = await _handler.HandleAsync(AdminMemberId, OrganizerId, request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
        _eventRepository.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WithValidRequest_RecordsCreatedByAndCreatedAt()
    {
        var (venueId, seatMapId) = SeedVenueAndSeatMap(seatCount: 1);
        var request = new CreateEventRequest("Concert", DateTime.UtcNow.AddDays(30), venueId, seatMapId);

        var result = await _handler.HandleAsync(AdminMemberId, OrganizerId, request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var createdEvent = _eventRepository.Data.Single(e => e.Id == result.Value);
        createdEvent.CreatedByMemberId.Should().Be(AdminMemberId);
        createdEvent.CreatedAtUtc.Should().Be(_dateTimeProvider.UtcNow);
    }

    // [EVT-CREATE-002] OrganizerId 一律取自呼叫端 Access Token 對應的 organizerId 參數，不接受請求內容指定或覆寫。
    [Fact]
    public async Task HandleAsync_WithValidRequest_RecordsOrganizerIdFromCallerContext()
    {
        var (venueId, seatMapId) = SeedVenueAndSeatMap(seatCount: 1);
        var request = new CreateEventRequest("Concert", DateTime.UtcNow.AddDays(30), venueId, seatMapId);

        var result = await _handler.HandleAsync(AdminMemberId, OrganizerId, request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _eventRepository.Data.Single(e => e.Id == result.Value).OrganizerId.Should().Be(OrganizerId);
    }

    [Fact]
    public async Task HandleAsync_WithSeatMapBelongingToAnotherVenue_ReturnsNotFound()
    {
        var (_, seatMapId) = SeedVenueAndSeatMap(seatCount: 1);
        var (otherVenueId, _) = SeedVenueAndSeatMap(seatCount: 1);
        var request = new CreateEventRequest("Concert", DateTime.UtcNow.AddDays(30), otherVenueId, seatMapId);

        var result = await _handler.HandleAsync(AdminMemberId, OrganizerId, request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
        _eventRepository.Data.Should().BeEmpty();
        _eventSeatRepository.Data.Should().BeEmpty();
    }

    // query-caching tasks.md 4.1／4.5：交易提交成功後才呼叫失效，驗證失敗不觸發失效。
    [Fact]
    public async Task HandleAsync_WithValidRequest_InvalidatesEventListCache()
    {
        var (venueId, seatMapId) = SeedVenueAndSeatMap(seatCount: 1);
        var request = new CreateEventRequest("Concert", DateTime.UtcNow.AddDays(30), venueId, seatMapId);

        var result = await _handler.HandleAsync(AdminMemberId, OrganizerId, request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _queryCache.RemoveCalls.Should().ContainSingle(key => key == "query-cache:events:list:v2");
    }

    [Fact]
    public async Task HandleAsync_WithBlankTitle_DoesNotInvalidateEventListCache()
    {
        var (venueId, seatMapId) = SeedVenueAndSeatMap(seatCount: 1);
        var request = new CreateEventRequest("  ", DateTime.UtcNow.AddDays(30), venueId, seatMapId);

        var result = await _handler.HandleAsync(AdminMemberId, OrganizerId, request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        _queryCache.RemoveCalls.Should().BeEmpty();
    }

    // EVT-REALNAME-001：旗標建立後不可變更（I1），建立時沒存進去就無法補救。
    [Fact]
    public async Task HandleAsync_WhenIsRealNameRequiredTrue_StoresTrue()
    {
        var (venueId, seatMapId) = SeedVenueAndSeatMap(seatCount: 1);
        var request = new CreateEventRequest("Concert", DateTime.UtcNow.AddDays(30), venueId, seatMapId, IsRealNameRequired: true);

        var result = await _handler.HandleAsync(AdminMemberId, OrganizerId, request, CancellationToken.None);

        _eventRepository.Data.Single(e => e.Id == result.Value).IsRealNameRequired.Should().BeTrue();
    }

    // EVT-REALNAME-002：既有客戶端不帶此欄位，不得因此建立出需實名活動。
    [Fact]
    public async Task HandleAsync_WhenIsRealNameRequiredOmitted_StoresFalse()
    {
        var (venueId, seatMapId) = SeedVenueAndSeatMap(seatCount: 1);
        var request = new CreateEventRequest("Concert", DateTime.UtcNow.AddDays(30), venueId, seatMapId);

        var result = await _handler.HandleAsync(AdminMemberId, OrganizerId, request, CancellationToken.None);

        _eventRepository.Data.Single(e => e.Id == result.Value).IsRealNameRequired.Should().BeFalse();
    }

    private CreateEventRequest CreateSalesWindowRequest(DateTime? salesStartAtUtc, DateTime? salesEndAtUtc)
    {
        var (venueId, seatMapId) = SeedVenueAndSeatMap(seatCount: 1);
        return new CreateEventRequest("Concert", _dateTimeProvider.UtcNow.AddDays(10), venueId, seatMapId,
            SalesStartAtUtc: salesStartAtUtc, SalesEndAtUtc: salesEndAtUtc);
    }

    private async Task AssertValidationWithoutAddingEventAsync(CreateEventRequest request)
    {
        var result = await _handler.HandleAsync(AdminMemberId, OrganizerId, request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        _eventRepository.Data.Should().BeEmpty();
        _eventSeatRepository.Data.Should().BeEmpty();
    }

    /// <summary>Kind 案例：Validator 錯誤只指向違反的欄位，且 Handler 回 Validation、未新增活動。</summary>
    private async Task AssertKindValidationOnlyOnAsync(CreateEventRequest request, string expectedPropertyName)
    {
        var validation = await new CreateEventRequestValidator().ValidateAsync(request);

        validation.IsValid.Should().BeFalse();
        validation.Errors.Should().OnlyContain(e => e.PropertyName == expectedPropertyName);
        await AssertValidationWithoutAddingEventAsync(request);
    }

    // EVT-SALES-001
    [Fact]
    public async Task HandleAsync_WithValidSalesWindow_PassesBothValuesToEvent()
    {
        var salesStart = _dateTimeProvider.UtcNow.AddDays(1);
        var salesEnd = _dateTimeProvider.UtcNow.AddDays(9);

        var result = await _handler.HandleAsync(AdminMemberId, OrganizerId, CreateSalesWindowRequest(salesStart, salesEnd), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var created = _eventRepository.Data.Single(e => e.Id == result.Value);
        created.SalesStartAtUtc.Should().Be(salesStart);
        created.SalesEndAtUtc.Should().Be(salesEnd);
    }

    // EVT-SALES-003
    [Fact]
    public async Task HandleAsync_WithOnlySalesStart_PassesNullSalesEndToEvent()
    {
        var salesStart = _dateTimeProvider.UtcNow.AddDays(1);

        var result = await _handler.HandleAsync(AdminMemberId, OrganizerId, CreateSalesWindowRequest(salesStart, null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var created = _eventRepository.Data.Single(e => e.Id == result.Value);
        created.SalesStartAtUtc.Should().Be(salesStart);
        created.SalesEndAtUtc.Should().BeNull();
    }

    // EVT-SALES-002：既有客戶端不帶新欄位
    [Fact]
    public async Task HandleAsync_WithoutSalesWindow_CreatesEventWithNullSalesWindow()
    {
        var (venueId, seatMapId) = SeedVenueAndSeatMap(seatCount: 1);
        var request = new CreateEventRequest("Concert", _dateTimeProvider.UtcNow.AddDays(10), venueId, seatMapId);

        var result = await _handler.HandleAsync(AdminMemberId, OrganizerId, request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var created = _eventRepository.Data.Single(e => e.Id == result.Value);
        created.SalesStartAtUtc.Should().BeNull();
        created.SalesEndAtUtc.Should().BeNull();
    }

    // EVT-SALES-007：不要求開賣時間晚於現在，允許建立後立即開賣
    [Fact]
    public async Task HandleAsync_WithPastSalesStart_CreatesEvent()
    {
        var request = CreateSalesWindowRequest(_dateTimeProvider.UtcNow.AddDays(-1), null);

        (await new CreateEventRequestValidator().ValidateAsync(request)).IsValid.Should().BeTrue();
        var result = await _handler.HandleAsync(AdminMemberId, OrganizerId, request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    // EVT-SALES-004
    [Fact]
    public async Task HandleAsync_WhenSalesEndIsAfterStartAt_ReturnsValidationWithoutAddingEvent()
        => await AssertValidationWithoutAddingEventAsync(CreateSalesWindowRequest(null, _dateTimeProvider.UtcNow.AddDays(10).AddTicks(1)));

    // EVT-SALES-005
    [Fact]
    public async Task HandleAsync_WhenSalesStartIsNotBeforeSalesEnd_ReturnsValidationWithoutAddingEvent()
    {
        var salesEnd = _dateTimeProvider.UtcNow.AddDays(9);
        await AssertValidationWithoutAddingEventAsync(CreateSalesWindowRequest(salesEnd, salesEnd));
    }

    // EVT-SALES-006
    [Fact]
    public async Task HandleAsync_WhenSalesStartIsNotBeforeStartAtWithoutSalesEnd_ReturnsValidationWithoutAddingEvent()
        => await AssertValidationWithoutAddingEventAsync(CreateSalesWindowRequest(_dateTimeProvider.UtcNow.AddDays(10), null));

    // EVT-SALES-008
    [Fact]
    public async Task HandleAsync_WhenSalesStartKindIsUnspecified_ReturnsValidationOnSalesStartAtUtc()
        => await AssertKindValidationOnlyOnAsync(
            CreateSalesWindowRequest(DateTime.SpecifyKind(_dateTimeProvider.UtcNow.AddDays(1), DateTimeKind.Unspecified), null),
            nameof(CreateEventRequest.SalesStartAtUtc));

    // EVT-SALES-013
    [Fact]
    public async Task HandleAsync_WhenSalesEndKindIsUnspecified_ReturnsValidationOnSalesEndAtUtc()
        => await AssertKindValidationOnlyOnAsync(
            CreateSalesWindowRequest(null, DateTime.SpecifyKind(_dateTimeProvider.UtcNow.AddDays(9), DateTimeKind.Unspecified)),
            nameof(CreateEventRequest.SalesEndAtUtc));

    // EVT-SALES-014
    [Fact]
    public async Task HandleAsync_WhenSalesStartKindIsLocal_ReturnsValidationOnSalesStartAtUtc()
        => await AssertKindValidationOnlyOnAsync(
            CreateSalesWindowRequest(DateTime.SpecifyKind(_dateTimeProvider.UtcNow.AddDays(1), DateTimeKind.Local), null),
            nameof(CreateEventRequest.SalesStartAtUtc));

    // EVT-SALES-016
    [Fact]
    public async Task HandleAsync_WhenSalesEndKindIsLocal_ReturnsValidationOnSalesEndAtUtc()
        => await AssertKindValidationOnlyOnAsync(
            CreateSalesWindowRequest(null, DateTime.SpecifyKind(_dateTimeProvider.UtcNow.AddDays(9), DateTimeKind.Local)),
            nameof(CreateEventRequest.SalesEndAtUtc));

    // EVT-SALES-015：兩欄位各自檢查 Kind，錯誤只指向非 UTC 的那一欄
    [Fact]
    public async Task HandleAsync_WhenOnlySalesEndKindIsNotUtc_ReturnsValidationOnlyOnSalesEndAtUtc()
        => await AssertKindValidationOnlyOnAsync(
            CreateSalesWindowRequest(_dateTimeProvider.UtcNow.AddDays(1), DateTime.SpecifyKind(_dateTimeProvider.UtcNow.AddDays(9), DateTimeKind.Unspecified)),
            nameof(CreateEventRequest.SalesEndAtUtc));

    // EVT-SALES-017：timestamptz 只存到微秒，同一微秒內「開賣 < 停售」寫入後會相等，EF 具現化 Event 時丟例外；
    // 每案在 100ns 精度下時間關係都合法，只違反精度。
    [Theory]
    [InlineData(1, new[] { nameof(CreateEventRequest.SalesStartAtUtc), nameof(CreateEventRequest.SalesEndAtUtc) })]
    [InlineData(2, new[] { nameof(CreateEventRequest.SalesStartAtUtc) })]
    [InlineData(3, new[] { nameof(CreateEventRequest.SalesEndAtUtc) })]
    [InlineData(4, new[] { nameof(CreateEventRequest.StartAtUtc) })]
    [InlineData(5, new[] { nameof(CreateEventRequest.StartAtUtc) })]
    public async Task HandleAsync_WhenSalesTimeHasSubMicrosecondPrecision_ReturnsValidationWithoutAddingEvent(int caseNumber, string[] expectedPropertyNames)
    {
        var (venueId, seatMapId) = SeedVenueAndSeatMap(seatCount: 1);
        var baseTime = _dateTimeProvider.UtcNow.AddDays(1);
        var startAt = _dateTimeProvider.UtcNow.AddDays(10);
        var request = caseNumber switch
        {
            1 => new CreateEventRequest("Concert", startAt, venueId, seatMapId,
                SalesStartAtUtc: baseTime.AddTicks(1), SalesEndAtUtc: baseTime.AddTicks(5)),
            2 => new CreateEventRequest("Concert", startAt, venueId, seatMapId, SalesStartAtUtc: baseTime.AddTicks(1)),
            3 => new CreateEventRequest("Concert", startAt, venueId, seatMapId, SalesEndAtUtc: baseTime.AddTicks(1)),
            4 => new CreateEventRequest("Concert", startAt.AddTicks(1), venueId, seatMapId, SalesStartAtUtc: baseTime),
            5 => new CreateEventRequest("Concert", startAt.AddTicks(1), venueId, seatMapId,
                SalesStartAtUtc: baseTime, SalesEndAtUtc: baseTime.AddDays(1)),
            _ => throw new ArgumentOutOfRangeException(nameof(caseNumber)),
        };

        var validation = await new CreateEventRequestValidator().ValidateAsync(request);

        validation.Errors.Select(e => e.PropertyName).Should().BeEquivalentTo(expectedPropertyNames);
        await AssertValidationWithoutAddingEventAsync(request);
    }

    // EVT-SALES-018：未提供開賣時間時不限制 StartAtUtc 精度，與本次變更前相容
    [Fact]
    public async Task HandleAsync_WhenStartAtHasSubMicrosecondPrecisionWithoutSalesWindow_CreatesEvent()
    {
        var (venueId, seatMapId) = SeedVenueAndSeatMap(seatCount: 1);
        var request = new CreateEventRequest("Concert", _dateTimeProvider.UtcNow.AddDays(10).AddTicks(1), venueId, seatMapId);

        var result = await _handler.HandleAsync(AdminMemberId, OrganizerId, request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _eventRepository.Data.Should().ContainSingle(e => e.Id == result.Value);
    }

    // EVT-SALES-019：Npgsql 把 DateTime.MinValue 存成 -infinity，讀回 Kind=Unspecified，EF 具現化 Event 時 Kind 檢查丟例外
    [Theory]
    [InlineData(true, nameof(CreateEventRequest.SalesStartAtUtc))]
    [InlineData(false, nameof(CreateEventRequest.SalesEndAtUtc))]
    public async Task HandleAsync_WhenSalesTimeIsMinValue_ReturnsValidationWithoutAddingEvent(bool isSalesStart, string expectedPropertyName)
    {
        var minValueUtc = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
        var request = isSalesStart
            ? CreateSalesWindowRequest(minValueUtc, null)
            : CreateSalesWindowRequest(null, minValueUtc);

        var validation = await new CreateEventRequestValidator().ValidateAsync(request);

        validation.Errors.Select(e => e.PropertyName).Should().BeEquivalentTo(new[] { expectedPropertyName });
        await AssertValidationWithoutAddingEventAsync(request);
    }
}
