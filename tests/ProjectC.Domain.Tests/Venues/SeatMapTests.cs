using FluentAssertions;
using ProjectC.Domain.Venues;

namespace ProjectC.Domain.Tests.Venues;

public class SeatMapTests
{
    [Fact]
    public void AddSeat_WhenZoneAndSeatNumberAreDistinct_AddsAllSeats()
    {
        var seatMap = new SeatMap(Guid.NewGuid(), Guid.NewGuid());

        seatMap.AddSeat("A", "1");
        seatMap.AddSeat("A", "2");
        seatMap.AddSeat("B", "1");

        seatMap.Seats.Should().HaveCount(3);
    }

    [Fact]
    public void AddSeat_WhenZoneAndSeatNumberAlreadyExist_ThrowsInvalidOperationException()
    {
        var seatMap = new SeatMap(Guid.NewGuid(), Guid.NewGuid());
        seatMap.AddSeat("A", "1");

        var act = () => seatMap.AddSeat("A", "1");

        act.Should().Throw<InvalidOperationException>();
        seatMap.Seats.Should().HaveCount(1);
    }

    // 訂單明細的 SeatZoneCode／SeatNumber「同時為 null 或同時有值」依賴此不變式：座位範本不會只缺分區或號碼，
    // 因此 Handler 不另寫部分缺失的判斷分支（order-display-enrichment design.md 決策 3）。
    [Theory]
    [InlineData(null, "1")]
    [InlineData("", "1")]
    [InlineData("   ", "1")]
    [InlineData("A", null)]
    [InlineData("A", "")]
    [InlineData("A", "   ")]
    public void AddSeat_WhenZoneCodeOrSeatNumberIsBlank_ThrowsArgumentException(string? zoneCode, string? seatNumber)
    {
        var seatMap = new SeatMap(Guid.NewGuid(), Guid.NewGuid());

        var act = () => seatMap.AddSeat(zoneCode!, seatNumber!);

        act.Should().Throw<ArgumentException>();
        seatMap.Seats.Should().BeEmpty();
    }
}
