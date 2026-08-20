using FluentAssertions;
using ProjectC.Domain.Tickets;

namespace ProjectC.Domain.Tests.Tickets;

public class TicketTests
{
    private static readonly DateTime IssuedAtUtc = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Constructor_WhenCreated_HasIssuedStatusAndRecordsIssuedAtUtc()
    {
        var ticket = new Ticket(Guid.NewGuid(), Guid.NewGuid(), IssuedAtUtc);

        ticket.Status.Should().Be(TicketStatus.Issued);
        ticket.IssuedAtUtc.Should().Be(IssuedAtUtc);
        ticket.RedeemedAtUtc.Should().BeNull();
    }

    [Fact]
    public void Redeem_WhenIssued_TransitionsToRedeemedAndRecordsRedeemedAtUtc()
    {
        var ticket = new Ticket(Guid.NewGuid(), Guid.NewGuid(), IssuedAtUtc);
        var redeemedAt = IssuedAtUtc.AddHours(1);

        ticket.Redeem(redeemedAt);

        ticket.Status.Should().Be(TicketStatus.Redeemed);
        ticket.RedeemedAtUtc.Should().Be(redeemedAt);
    }

    [Fact]
    public void Redeem_WhenAlreadyRedeemed_ThrowsTicketNotIssuedException()
    {
        var ticket = new Ticket(Guid.NewGuid(), Guid.NewGuid(), IssuedAtUtc);
        ticket.Redeem(IssuedAtUtc.AddHours(1));

        var act = () => ticket.Redeem(IssuedAtUtc.AddHours(2));

        act.Should().Throw<TicketNotIssuedException>();
        ticket.Status.Should().Be(TicketStatus.Redeemed);
    }
}
