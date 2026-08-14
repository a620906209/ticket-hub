using ProjectC.Domain.Common;

namespace ProjectC.Application.Tests.TestSupport;

public sealed class FakeDateTimeProvider(DateTime utcNow) : IDateTimeProvider
{
    public DateTime UtcNow { get; set; } = utcNow;
}
