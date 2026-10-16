using FluentValidation;

namespace ProjectC.Application.Venues.CreateSeatMap;

public sealed class CreateSeatMapRequestValidator : AbstractValidator<CreateSeatMapRequest>
{
    public CreateSeatMapRequestValidator()
    {
        RuleFor(x => x.Seats).NotEmpty();
        RuleFor(x => x.Seats.Count)
            .LessThanOrEqualTo(SeatMapLimits.MaxSeats)
            .When(x => x.Seats is not null)
            .OverridePropertyName(nameof(CreateSeatMapRequest.Seats))
            .WithMessage($"Seats must not contain more than {SeatMapLimits.MaxSeats} items.");
        // 超過上限時不逐筆驗證，避免對 10MB 本文跑數萬次子 Validator 並產生同等數量的錯誤訊息
        // （request-body-size-limits design.md 決策 7）。
        RuleForEach(x => x.Seats)
            .NotNull()
            .WithMessage("Seats must not contain null items.")
            .SetValidator(new SeatRequestValidator())
            .When(x => x.Seats is null || x.Seats.Count <= SeatMapLimits.MaxSeats);
    }
}

public sealed class SeatRequestValidator : AbstractValidator<SeatRequest>
{
    public SeatRequestValidator()
    {
        RuleFor(x => x.ZoneCode).NotEmpty().MaximumLength(50);
        RuleFor(x => x.SeatNumber).NotEmpty().MaximumLength(50);
    }
}
