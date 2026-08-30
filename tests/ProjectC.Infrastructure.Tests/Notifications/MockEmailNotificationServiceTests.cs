using FluentAssertions;
using ProjectC.Infrastructure.Notifications;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests.Notifications;

public class MockEmailNotificationServiceTests
{
    private const string ToEmail = "buyer@example.com";
    private const string EventTitle = "Concert";

    [Fact]
    public async Task NotifyTicketsIssuedAsync_WhenAlwaysSucceedIsTrue_CompletesAndLogsMaskedFields()
    {
        var logger = new ListLogger<MockEmailNotificationService>();
        var service = new MockEmailNotificationService(new MockEmailNotificationServiceOptions { AlwaysSucceed = true }, logger);
        var orderId = Guid.NewGuid();

        await service.NotifyTicketsIssuedAsync(ToEmail, EventTitle, orderId, ticketCount: 3, CancellationToken.None);

        logger.Entries.Should().ContainSingle();
        var entry = logger.Entries.Single();
        entry.State["ToEmail"].Should().Be(EmailMasker.Mask(ToEmail));
        entry.State["EventTitle"].Should().Be(EventTitle);
        entry.State["OrderId"].Should().Be(orderId);
        entry.State["TicketCount"].Should().Be(3);
    }

    [Fact]
    public async Task NotifyTicketsIssuedAsync_WhenAlwaysSucceedIsFalse_ThrowsException()
    {
        var service = new MockEmailNotificationService(
            new MockEmailNotificationServiceOptions { AlwaysSucceed = false }, new ListLogger<MockEmailNotificationService>());

        var act = () => service.NotifyTicketsIssuedAsync(ToEmail, EventTitle, Guid.NewGuid(), ticketCount: 1, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task NotifyTicketsIssuedAsync_WhenAlwaysSucceedIsTrue_LoggedToEmailFieldIsNotTheFullEmail()
    {
        var logger = new ListLogger<MockEmailNotificationService>();
        var service = new MockEmailNotificationService(new MockEmailNotificationServiceOptions { AlwaysSucceed = true }, logger);

        await service.NotifyTicketsIssuedAsync(ToEmail, EventTitle, Guid.NewGuid(), ticketCount: 1, CancellationToken.None);

        logger.Entries.Single().State["ToEmail"].Should().NotBe(ToEmail);
    }

    [Fact]
    public async Task NotifyTicketsIssuedAsync_WhenAlwaysSucceedIsFalse_ExceptionMessageDoesNotContainTheFullEmail()
    {
        var service = new MockEmailNotificationService(
            new MockEmailNotificationServiceOptions { AlwaysSucceed = false }, new ListLogger<MockEmailNotificationService>());

        var act = () => service.NotifyTicketsIssuedAsync(ToEmail, EventTitle, Guid.NewGuid(), ticketCount: 1, CancellationToken.None);

        var assertion = await act.Should().ThrowAsync<Exception>();
        assertion.Which.Message.Should().NotContain(ToEmail);
    }
}
