using FluentAssertions;
using ProjectC.Application.Venues.CreateSeatMap;

namespace ProjectC.Application.Tests.Venues.CreateSeatMap;

// request-body-size-limits design.md 決策 7：座位數上限讓 10MB 本文的工作量有界；超過上限時只回一筆座位數錯誤，
// 不逐筆驗證（否則 50,001 席會跑同等次數的子 Validator、產生同等數量的錯誤訊息）。
public class CreateSeatMapRequestValidatorTests
{
    private readonly CreateSeatMapRequestValidator _validator = new();

    [Fact]
    public void Validate_WhenSeatsExceedMax_ReturnsOnlyCountError()
    {
        // 含重複座位、空白與 null ZoneCode：若逐筆驗證仍執行，會多出欄位錯誤，讓「只有一筆」的斷言失敗。
        var seats = Enumerable.Range(1, SeatMapLimits.MaxSeats - 2)
            .Select(i => new SeatRequest("A", i.ToString()))
            .Append(new SeatRequest("A", "1"))
            .Append(new SeatRequest("", "blank-zone"))
            .Append(new SeatRequest(null, "null-zone"))
            .ToList();
        seats.Should().HaveCount(SeatMapLimits.MaxSeats + 1);

        var result = _validator.Validate(new CreateSeatMapRequest(seats));

        result.Errors.Should().ContainSingle();
        result.Errors[0].PropertyName.Should().Be(nameof(CreateSeatMapRequest.Seats));
        result.Errors[0].ErrorMessage.Should().Be($"Seats must not contain more than {SeatMapLimits.MaxSeats} items.");
    }

    [Fact]
    public void Validate_WhenSeatsEqualMax_IsValid()
    {
        var seats = Enumerable.Range(1, SeatMapLimits.MaxSeats)
            .Select(i => new SeatRequest("A", i.ToString()))
            .ToList();

        var result = _validator.Validate(new CreateSeatMapRequest(seats));

        result.IsValid.Should().BeTrue();
    }

    // 逐筆驗證的跳過條件是「超過」上限；恰好上限時若誤寫成 < 而跳過，null 元素會進到 Handler 的 GroupBy 回 500。
    [Fact]
    public void Validate_WhenSeatsEqualMaxAndContainInvalidItems_ReturnsItemErrors()
    {
        var seats = Enumerable.Range(1, SeatMapLimits.MaxSeats - 2)
            .Select(i => new SeatRequest("A", i.ToString()))
            .Append(new SeatRequest(null, "null-zone"))
            .Append(null!)
            .ToList();
        seats.Should().HaveCount(SeatMapLimits.MaxSeats);

        var result = _validator.Validate(new CreateSeatMapRequest(seats));

        result.Errors.Should().Contain(e => e.ErrorMessage == "Seats must not contain null items.");
        result.Errors.Should().Contain(e => e.PropertyName.EndsWith(nameof(SeatRequest.ZoneCode)));
    }

    // spec 以字面值規定上限；其他測試都引用常數計算邊界，常數被改時會跟著移動，所以這裡釘住字面值。
    [Fact]
    public void MaxSeats_MatchesSpecifiedLimit()
    {
        SeatMapLimits.MaxSeats.Should().Be(50_000);
    }

    // 沒有 NotNull 時 FluentValidation 會略過 null 子元素、判為有效，null 進到 Handler 的 GroupBy 回 500。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Validate_WhenSeatsContainNull_ReturnsNullItemError(bool hasValidSeat)
    {
        var seats = new List<SeatRequest>();
        if (hasValidSeat)
        {
            seats.Add(new SeatRequest("A", "1"));
        }
        seats.Add(null!);

        var result = _validator.Validate(new CreateSeatMapRequest(seats));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Seats must not contain null items.");
    }
}
