using ProjectC.Application.Common.Interfaces;

namespace ProjectC.Application.Tests.TestSupport;

public sealed class FakeDateTimeProvider : IDateTimeProvider
{
    public DateTime UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
}
