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
}
