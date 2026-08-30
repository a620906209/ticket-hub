namespace ProjectC.Infrastructure.Notifications;

public sealed class MockEmailNotificationServiceOptions
{
    public const string SectionName = "MockEmailNotificationService";

    public bool AlwaysSucceed { get; set; } = true;
}
