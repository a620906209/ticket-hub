using ProjectC.Application.Common.Interfaces;

namespace ProjectC.Infrastructure.Tests.TestSupport;

public sealed class FakeDateTimeProvider : IDateTimeProvider
{
    public DateTime UtcNow { get; set; } = DateTime.UtcNow;
}
