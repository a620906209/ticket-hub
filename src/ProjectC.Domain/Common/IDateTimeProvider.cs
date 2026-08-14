namespace ProjectC.Domain.Common;

public interface IDateTimeProvider
{
    DateTime UtcNow { get; }
}
