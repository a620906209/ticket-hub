using ProjectC.Application.Common.Interfaces;

namespace ProjectC.Infrastructure.Security;

public class SystemDateTimeProvider : IDateTimeProvider
{
    public DateTime UtcNow => DateTime.UtcNow;
}
