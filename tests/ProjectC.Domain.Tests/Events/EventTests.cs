using FluentAssertions;
using ProjectC.Domain.Events;
using ProjectC.Domain.Venues;

namespace ProjectC.Domain.Tests.Events;

public class EventTests
{
    private static SeatMap CreateSeatMap(int seatCount)
    {
        var seatMap = new SeatMap(Guid.NewGuid(), Guid.NewGuid());
        for (var i = 1; i <= seatCount; i++)
            seatMap.AddSeat("A", i.ToString());

        return seatMap;
    }

    [Fact]
    public void Constructor_WhenAllRequiredFieldsProvided_CreatesEvent()
    {
        var seatMap = CreateSeatMap(1);

        var @event = new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(30), Guid.NewGuid(), seatMap.Id, Guid.NewGuid());

        @event.Title.Should().Be("Concert");
    }

    // 既有呼叫端不帶參數時不得意外變成需實名（EVT-REALNAME-002 的 Domain 部分）。
    [Fact]
    public void Constructor_WhenIsRealNameRequiredNotSpecified_DefaultsToFalse()
    {
        var @event = new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(30), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        @event.IsRealNameRequired.Should().BeFalse();
    }

    [Fact]
    public void Constructor_WhenIsRealNameRequiredTrue_RecordsTrue()
    {
        var @event = new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(30), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), isRealNameRequired: true);

        @event.IsRealNameRequired.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_WhenTitleIsMissing_ThrowsArgumentException(string title)
    {
        var act = () => new Event(Guid.NewGuid(), title, DateTime.UtcNow.AddDays(30), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_WhenStartTimeIsMissing_ThrowsArgumentException()
    {
        var act = () => new Event(Guid.NewGuid(), "Concert", default, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        act.Should().Throw<ArgumentException>();
    }

    // event-management-organizer-scoping tasks.md 1.1：Event 必定歸屬某個 Organizer，
    // 空 Guid 代表呼叫端沒有取得有效的 Organizer context，必須在 Domain 層擋下，不能寫入一筆無主活動。
    [Fact]
    public void Constructor_WhenOrganizerIdIsEmpty_ThrowsArgumentException()
    {
        var act = () => new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(30), Guid.NewGuid(), Guid.NewGuid(), Guid.Empty);

        act.Should().Throw<ArgumentException>().WithParameterName("organizerId");
    }

    [Fact]
    public void Constructor_WhenOrganizerIdProvided_RecordsOrganizerId()
    {
        var organizerId = Guid.NewGuid();

        var @event = new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(30), Guid.NewGuid(), Guid.NewGuid(), organizerId);

        @event.OrganizerId.Should().Be(organizerId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_WhenMaxTicketsPerOrderIsNotPositive_ThrowsArgumentException(int maxTicketsPerOrder)
    {
        var act = () => new Event(
            Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(30), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            maxTicketsPerOrder: maxTicketsPerOrder);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_WhenMaxTicketsPerOrderIsNull_AllowsUnlimitedTicketsPerOrder()
    {
        var @event = new Event(
            Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(30), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            maxTicketsPerOrder: null);

        @event.MaxTicketsPerOrder.Should().BeNull();
    }

    [Fact]
    public void CreateEventSeats_WhenSeatMapHasNSeats_CreatesNAvailableEventSeats()
    {
        var seatMap = CreateSeatMap(3);
        var @event = new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(30), Guid.NewGuid(), seatMap.Id, Guid.NewGuid());

        var eventSeats = @event.CreateEventSeats(seatMap);

        eventSeats.Should().HaveCount(3);
        eventSeats.Should().OnlyContain(seat => seat.GetStatus(DateTime.UtcNow) == EventSeatStatus.Available);
        eventSeats.Select(s => s.SeatId).Should().BeEquivalentTo(seatMap.Seats.Select(s => s.Id));
    }

    [Fact]
    public void CreateEventSeats_ForTwoEventsSharingSameSeatMap_ProducesIndependentInventory()
    {
        var seatMap = CreateSeatMap(1);
        var eventA = new Event(Guid.NewGuid(), "Show A", DateTime.UtcNow.AddDays(10), Guid.NewGuid(), seatMap.Id, Guid.NewGuid());
        var eventB = new Event(Guid.NewGuid(), "Show B", DateTime.UtcNow.AddDays(20), Guid.NewGuid(), seatMap.Id, Guid.NewGuid());

        var seatsA = eventA.CreateEventSeats(seatMap);
        var seatsB = eventB.CreateEventSeats(seatMap);

        var now = DateTime.UtcNow;
        var orderId = Guid.NewGuid();
        seatsA[0].Hold(orderId, now.AddMinutes(10), now);
        seatsA[0].ConfirmSold(orderId, now);

        seatsA[0].GetStatus(now).Should().Be(EventSeatStatus.Sold);
        seatsB[0].GetStatus(now).Should().Be(EventSeatStatus.Available);
        seatsA[0].Id.Should().NotBe(seatsB[0].Id);
    }

    [Fact]
    public void CreateEventSeats_EachSeatTemplateMapsToExactlyOneEventSeat()
    {
        var seatMap = CreateSeatMap(5);
        var @event = new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(30), Guid.NewGuid(), seatMap.Id, Guid.NewGuid());

        var eventSeats = @event.CreateEventSeats(seatMap);

        eventSeats.Select(s => s.SeatId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void CreateEventSeats_WhenSeatMapDoesNotBelongToEvent_ThrowsArgumentException()
    {
        var seatMap = CreateSeatMap(1);
        var otherSeatMap = CreateSeatMap(1);
        var @event = new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(30), Guid.NewGuid(), seatMap.Id, Guid.NewGuid());

        var act = () => @event.CreateEventSeats(otherSeatMap);

        act.Should().Throw<ArgumentException>();
    }

    // ---- 熱門搶購模式開關（rate-limiting-queue design.md 決策 2） ----

    [Fact]
    public void Constructor_DefaultsIsQueueModeEnabledToFalse()
    {
        var @event = new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(30), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        @event.IsQueueModeEnabled.Should().BeFalse();
    }

    [Fact]
    public void EnableQueueMode_SetsIsQueueModeEnabledToTrue()
    {
        var @event = new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(30), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        @event.EnableQueueMode();

        @event.IsQueueModeEnabled.Should().BeTrue();
    }

    [Fact]
    public void DisableQueueMode_AfterEnabled_SetsIsQueueModeEnabledToFalse()
    {
        var @event = new Event(Guid.NewGuid(), "Concert", DateTime.UtcNow.AddDays(30), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        @event.EnableQueueMode();

        @event.DisableQueueMode();

        @event.IsQueueModeEnabled.Should().BeFalse();
    }

    private static readonly DateTime SalesBaseUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Event CreateEventWithSalesWindow(DateTime startAtUtc, DateTime? salesStartAtUtc, DateTime? salesEndAtUtc)
        => new(Guid.NewGuid(), "Concert", startAtUtc, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            salesStartAtUtc: salesStartAtUtc, salesEndAtUtc: salesEndAtUtc);

    // EVT-SALES-001
    [Fact]
    public void Constructor_WithValidSalesWindow_SetsBothProperties()
    {
        var salesStart = SalesBaseUtc.AddDays(1);
        var salesEnd = SalesBaseUtc.AddDays(9);

        var @event = CreateEventWithSalesWindow(SalesBaseUtc.AddDays(10), salesStart, salesEnd);

        @event.SalesStartAtUtc.Should().Be(salesStart);
        @event.SalesEndAtUtc.Should().Be(salesEnd);
    }

    // EVT-SALES-002：既有呼叫端不帶參數時不得意外出現販售期間
    [Fact]
    public void Constructor_WithoutSalesWindow_LeavesBothPropertiesNull()
    {
        var @event = new Event(Guid.NewGuid(), "Concert", SalesBaseUtc.AddDays(10), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        @event.SalesStartAtUtc.Should().BeNull();
        @event.SalesEndAtUtc.Should().BeNull();
    }

    // EVT-SALES-003
    [Fact]
    public void Constructor_WithOnlySalesStart_LeavesSalesEndNull()
    {
        var @event = CreateEventWithSalesWindow(SalesBaseUtc.AddDays(10), SalesBaseUtc.AddDays(1), null);

        @event.SalesStartAtUtc.Should().Be(SalesBaseUtc.AddDays(1));
        @event.SalesEndAtUtc.Should().BeNull();
    }

    // EVT-SALES-004
    [Fact]
    public void Constructor_WhenSalesEndIsAfterStartAt_ThrowsArgumentException()
    {
        var startAt = SalesBaseUtc.AddDays(10);

        var act = () => CreateEventWithSalesWindow(startAt, null, startAt.AddTicks(1));

        act.Should().Throw<ArgumentException>();
    }

    // EVT-SALES-004：邊界，停售時間等於活動開始時間合法
    [Fact]
    public void Constructor_WhenSalesEndEqualsStartAt_Succeeds()
    {
        var startAt = SalesBaseUtc.AddDays(10);

        var @event = CreateEventWithSalesWindow(startAt, null, startAt);

        @event.SalesEndAtUtc.Should().Be(startAt);
    }

    // EVT-SALES-005
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Constructor_WhenSalesStartIsNotBeforeSalesEnd_ThrowsArgumentException(int salesStartOffsetFromSalesEndTicks)
    {
        var salesEnd = SalesBaseUtc.AddDays(9);

        var act = () => CreateEventWithSalesWindow(SalesBaseUtc.AddDays(10), salesEnd.AddTicks(salesStartOffsetFromSalesEndTicks), salesEnd);

        act.Should().Throw<ArgumentException>();
    }

    // EVT-SALES-006
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Constructor_WhenSalesEndIsNullAndSalesStartIsNotBeforeStartAt_ThrowsArgumentException(int salesStartOffsetFromStartAtTicks)
    {
        var startAt = SalesBaseUtc.AddDays(10);

        var act = () => CreateEventWithSalesWindow(startAt, startAt.AddTicks(salesStartOffsetFromStartAtTicks), null);

        act.Should().Throw<ArgumentException>();
    }

    // EVT-SALES-007：Domain 不依當下時間驗證，過去的開賣時間合法
    [Fact]
    public void Constructor_WhenSalesStartIsInPast_Succeeds()
    {
        var pastSalesStart = SalesBaseUtc.AddYears(-1);

        var @event = CreateEventWithSalesWindow(SalesBaseUtc.AddDays(10), pastSalesStart, null);

        @event.SalesStartAtUtc.Should().Be(pastSalesStart);
    }

    // EVT-SALES-008
    [Fact]
    public void Constructor_WhenSalesStartKindIsUnspecified_ThrowsArgumentException()
    {
        var salesStart = DateTime.SpecifyKind(SalesBaseUtc.AddDays(1), DateTimeKind.Unspecified);

        var act = () => CreateEventWithSalesWindow(SalesBaseUtc.AddDays(10), salesStart, null);

        act.Should().Throw<ArgumentException>();
    }

    // EVT-SALES-013
    [Fact]
    public void Constructor_WhenSalesEndKindIsUnspecified_ThrowsArgumentException()
    {
        var salesEnd = DateTime.SpecifyKind(SalesBaseUtc.AddDays(9), DateTimeKind.Unspecified);

        var act = () => CreateEventWithSalesWindow(SalesBaseUtc.AddDays(10), null, salesEnd);

        act.Should().Throw<ArgumentException>();
    }

    // EVT-SALES-014
    [Fact]
    public void Constructor_WhenSalesStartKindIsLocal_ThrowsArgumentException()
    {
        var salesStart = DateTime.SpecifyKind(SalesBaseUtc.AddDays(1), DateTimeKind.Local);

        var act = () => CreateEventWithSalesWindow(SalesBaseUtc.AddDays(10), salesStart, null);

        act.Should().Throw<ArgumentException>();
    }

    // EVT-SALES-016
    [Fact]
    public void Constructor_WhenSalesEndKindIsLocal_ThrowsArgumentException()
    {
        var salesEnd = DateTime.SpecifyKind(SalesBaseUtc.AddDays(9), DateTimeKind.Local);

        var act = () => CreateEventWithSalesWindow(SalesBaseUtc.AddDays(10), null, salesEnd);

        act.Should().Throw<ArgumentException>();
    }

    // EVT-SALES-015：兩欄位各自檢查 Kind，不因另一欄位為 Utc 而放行
    [Fact]
    public void Constructor_WhenOnlySalesEndKindIsNotUtc_ThrowsArgumentException()
    {
        var salesStart = SalesBaseUtc.AddDays(1);
        var salesEnd = DateTime.SpecifyKind(SalesBaseUtc.AddDays(9), DateTimeKind.Unspecified);

        var act = () => CreateEventWithSalesWindow(SalesBaseUtc.AddDays(10), salesStart, salesEnd);

        act.Should().Throw<ArgumentException>();
    }

    // 決策 1 的左閉右開區間；支撐 TP-SALES-ORDER-001…005、PQ-SALES-JOIN-010／011 的 Domain 面
    public static TheoryData<string, DateTime?, DateTime?, DateTime, EventSalesStatus> SalesStatusBoundaryCases()
    {
        var startAt = SalesBaseUtc.AddDays(10);
        var salesStart = SalesBaseUtc.AddDays(1);
        var salesEnd = SalesBaseUtc.AddDays(9);

        return new TheoryData<string, DateTime?, DateTime?, DateTime, EventSalesStatus>
        {
            { "開賣前 1 tick", salesStart, salesEnd, salesStart.AddTicks(-1), EventSalesStatus.NotOpen },
            { "開賣當下（左閉）", salesStart, salesEnd, salesStart, EventSalesStatus.Open },
            { "停售前 1 tick", salesStart, salesEnd, salesEnd.AddTicks(-1), EventSalesStatus.Open },
            { "停售當下（右開）", salesStart, salesEnd, salesEnd, EventSalesStatus.Closed },
            { "未設停售，活動開始當下", salesStart, null, startAt, EventSalesStatus.Closed },
            { "未設停售，活動開始前 1 tick", salesStart, null, startAt.AddTicks(-1), EventSalesStatus.Open },
            { "皆未設定，活動開始前", null, null, SalesBaseUtc, EventSalesStatus.Open },
        };
    }

    // TP-SALES-ORDER-001…005、PQ-SALES-JOIN-010／011：Domain 面（販售區間 [SalesStart ?? -∞, SalesEnd ?? StartAt) 的邊界）。
    [Theory]
    [MemberData(nameof(SalesStatusBoundaryCases))]
    public void GetSalesStatus_AtEachBoundary_ReturnsExpectedStatus(
        string caseName, DateTime? salesStartAtUtc, DateTime? salesEndAtUtc, DateTime nowUtc, EventSalesStatus expected)
    {
        var @event = CreateEventWithSalesWindow(SalesBaseUtc.AddDays(10), salesStartAtUtc, salesEndAtUtc);

        @event.GetSalesStatus(nowUtc).Should().Be(expected, caseName);
    }
}
