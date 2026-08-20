using FluentAssertions;
using ProjectC.Application.Common;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Application.Tickets.RedeemTicket;
using ProjectC.Domain.Tickets;

namespace ProjectC.Application.Tests.Tickets.RedeemTicket;

public class RedeemTicketHandlerTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    private sealed class Fixture
    {
        public FakeTicketRepository TicketRepository { get; } = new();
        public FakeUnitOfWork UnitOfWork { get; } = new();
        public FakeDateTimeProvider DateTimeProvider { get; } = new() { UtcNow = Now };

        public RedeemTicketHandler CreateHandler() => new(TicketRepository, UnitOfWork, DateTimeProvider);
    }

    [Fact]
    public async Task HandleAsync_WhenTicketIsIssued_TransitionsToRedeemedAndRecordsRedeemedAtUtc()
    {
        var fixture = new Fixture();
        var ticket = new Ticket(Guid.NewGuid(), Guid.NewGuid(), Now.AddDays(-1));
        fixture.TicketRepository.Data.Add(ticket);

        var result = await fixture.CreateHandler().HandleAsync(ticket.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        ticket.Status.Should().Be(TicketStatus.Redeemed);
        ticket.RedeemedAtUtc.Should().Be(Now);
        fixture.UnitOfWork.LastTransaction!.Committed.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WhenTicketAlreadyRedeemed_ReturnsConflictAndDoesNotChangeState()
    {
        var fixture = new Fixture();
        var ticket = new Ticket(Guid.NewGuid(), Guid.NewGuid(), Now.AddDays(-1));
        ticket.Redeem(Now.AddHours(-1));
        var redeemedAt = ticket.RedeemedAtUtc;
        fixture.TicketRepository.Data.Add(ticket);

        var result = await fixture.CreateHandler().HandleAsync(ticket.Id, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Conflict);
        ticket.Status.Should().Be(TicketStatus.Redeemed);
        ticket.RedeemedAtUtc.Should().Be(redeemedAt);
        fixture.UnitOfWork.LastTransaction!.Committed.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_WhenTicketDoesNotExist_ReturnsNotFound()
    {
        var fixture = new Fixture();

        var result = await fixture.CreateHandler().HandleAsync(Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
        fixture.UnitOfWork.LastTransaction!.Committed.Should().BeFalse();
    }
}
