using FluentValidation;

namespace ProjectC.Application.Events.CreateEvent;

public sealed class CreateEventRequestValidator : AbstractValidator<CreateEventRequest>
{
    public CreateEventRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        RuleFor(x => x.StartAtUtc).NotEqual(default(DateTime));
        RuleFor(x => x.VenueId).NotEqual(Guid.Empty);
        RuleFor(x => x.SeatMapId).NotEqual(Guid.Empty);
        RuleFor(x => x.Description).MaximumLength(2000);
        RuleFor(x => x.PosterUrl).MaximumLength(500);
        RuleFor(x => x.MaxTicketsPerOrder).GreaterThan(0).When(x => x.MaxTicketsPerOrder.HasValue);

        // 與 Event 建構子相同的規則：在這裡先擋成 400，避免走到 Domain 的 ArgumentException 變成 500；
        // Kind 檢查也避免非 UTC 值寫入 timestamptz 時才失敗（event-sales-window design.md 決策 2）。
        RuleFor(x => x.SalesStartAtUtc)
            .Must(value => value!.Value.Kind == DateTimeKind.Utc)
            .When(x => x.SalesStartAtUtc.HasValue)
            .WithMessage("SalesStartAtUtc must be a UTC time (with 'Z' designator).");
        RuleFor(x => x.SalesEndAtUtc)
            .Must(value => value!.Value.Kind == DateTimeKind.Utc)
            .When(x => x.SalesEndAtUtc.HasValue)
            .WithMessage("SalesEndAtUtc must be a UTC time (with 'Z' designator).");
        RuleFor(x => x.SalesEndAtUtc)
            .Must((request, salesEndAtUtc) => salesEndAtUtc <= request.StartAtUtc)
            .When(x => x.SalesEndAtUtc.HasValue)
            .WithMessage("SalesEndAtUtc must not be after StartAtUtc.");
        RuleFor(x => x.SalesStartAtUtc)
            .Must((request, salesStartAtUtc) => salesStartAtUtc < (request.SalesEndAtUtc ?? request.StartAtUtc))
            .When(x => x.SalesStartAtUtc.HasValue)
            .WithMessage("SalesStartAtUtc must be before the effective sales end time.");

        // timestamptz 只存到微秒、寫入時截斷：同一微秒內的「開賣 < 停售」寫入後會相等，EF 以建構子具現化時丟例外、
        // 連帶讓活動列表 500。選擇拒絕而非截斷，不靜默改動輸入；未設開賣時不限制 StartAtUtc，維持相容
        // （event-sales-window design.md 決策 2「精度」）。
        RuleFor(x => x.SalesStartAtUtc)
            .Must(value => IsWholeMicrosecond(value!.Value))
            .When(x => x.SalesStartAtUtc.HasValue)
            .WithMessage("SalesStartAtUtc must not have sub-microsecond precision.");
        RuleFor(x => x.SalesEndAtUtc)
            .Must(value => IsWholeMicrosecond(value!.Value))
            .When(x => x.SalesEndAtUtc.HasValue)
            .WithMessage("SalesEndAtUtc must not have sub-microsecond precision.");
        RuleFor(x => x.StartAtUtc)
            .Must(IsWholeMicrosecond)
            .When(x => x.SalesStartAtUtc.HasValue)
            .WithMessage("StartAtUtc must not have sub-microsecond precision when SalesStartAtUtc is set.");

        // Npgsql 預設的 infinity 轉換把 DateTime.MinValue 存成 -infinity，讀回為 Kind=Unspecified，
        // EF 具現化 Event 時 Kind 檢查丟例外（event-sales-window design.md 決策 2「最小日期」）。
        RuleFor(x => x.SalesStartAtUtc)
            .NotEqual(DateTime.MinValue)
            .When(x => x.SalesStartAtUtc.HasValue)
            .WithMessage("SalesStartAtUtc must not be the minimum date.");
        RuleFor(x => x.SalesEndAtUtc)
            .NotEqual(DateTime.MinValue)
            .When(x => x.SalesEndAtUtc.HasValue)
            .WithMessage("SalesEndAtUtc must not be the minimum date.");
    }

    private static bool IsWholeMicrosecond(DateTime value) => value.Ticks % TimeSpan.TicksPerMicrosecond == 0;
}
