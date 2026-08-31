using FluentAssertions;
using Moq;
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
        public Mock<ITicketSigningService> TicketSigningService { get; } = new();

        public RedeemTicketHandler CreateHandler()
            => new(TicketRepository, UnitOfWork, DateTimeProvider, TicketSigningService.Object);
    }

    // 對應 AC: TICKET-REDEEM-SIG-BACKWARD-COMPAT（未提供簽章，成功/404/409 三種既有案例行為不變）
    [Fact]
    public async Task HandleAsync_WhenSignatureNotProvidedAndTicketIsIssued_TransitionsToRedeemedAndRecordsRedeemedAtUtc()
    {
        var fixture = new Fixture();
        var ticket = new Ticket(Guid.NewGuid(), Guid.NewGuid(), Now.AddDays(-1));
        fixture.TicketRepository.Data.Add(ticket);

        var result = await fixture.CreateHandler().HandleAsync(ticket.Id, null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        ticket.Status.Should().Be(TicketStatus.Redeemed);
        ticket.RedeemedAtUtc.Should().Be(Now);
        fixture.UnitOfWork.LastTransaction!.Committed.Should().BeTrue();
        fixture.TicketSigningService.Verify(s => s.TryVerify(It.IsAny<string>(), out It.Ref<Guid>.IsAny), Times.Never);
    }

    // 對應 AC: TICKET-REDEEM-SIG-BACKWARD-COMPAT
    [Fact]
    public async Task HandleAsync_WhenSignatureNotProvidedAndTicketAlreadyRedeemed_ReturnsConflictAndDoesNotChangeState()
    {
        var fixture = new Fixture();
        var ticket = new Ticket(Guid.NewGuid(), Guid.NewGuid(), Now.AddDays(-1));
        ticket.Redeem(Now.AddHours(-1));
        var redeemedAt = ticket.RedeemedAtUtc;
        fixture.TicketRepository.Data.Add(ticket);

        var result = await fixture.CreateHandler().HandleAsync(ticket.Id, null, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Conflict);
        ticket.Status.Should().Be(TicketStatus.Redeemed);
        ticket.RedeemedAtUtc.Should().Be(redeemedAt);
        fixture.UnitOfWork.LastTransaction!.Committed.Should().BeFalse();
    }

    // 對應 AC: TICKET-REDEEM-SIG-BACKWARD-COMPAT
    [Fact]
    public async Task HandleAsync_WhenSignatureNotProvidedAndTicketDoesNotExist_ReturnsNotFound()
    {
        var fixture = new Fixture();

        var result = await fixture.CreateHandler().HandleAsync(Guid.NewGuid(), null, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
        fixture.UnitOfWork.LastTransaction!.Committed.Should().BeFalse();
    }

    // 對應 AC: TICKET-REDEEM-SIG-VALID（正確簽章成功核銷）
    [Fact]
    public async Task HandleAsync_WhenSignatureValid_RedeemsTicket()
    {
        var fixture = new Fixture();
        var ticket = new Ticket(Guid.NewGuid(), Guid.NewGuid(), Now.AddDays(-1));
        fixture.TicketRepository.Data.Add(ticket);
        fixture.TicketSigningService
            .Setup(s => s.TryVerify(It.IsAny<string>(), out It.Ref<Guid>.IsAny))
            .Returns(true);

        var result = await fixture.CreateHandler().HandleAsync(ticket.Id, "valid-signature", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        ticket.Status.Should().Be(TicketStatus.Redeemed);
    }

    // 對應 AC: TICKET-REDEEM-SIG-INVALID（竄改簽章回傳 InvalidTicketSignature，未呼叫 GetForUpdateAsync）
    [Fact]
    public async Task HandleAsync_WhenSignatureInvalid_ReturnsInvalidTicketSignatureAndDoesNotLockTicket()
    {
        var fixture = new Fixture();
        var ticket = new Ticket(Guid.NewGuid(), Guid.NewGuid(), Now.AddDays(-1));
        fixture.TicketRepository.Data.Add(ticket);
        fixture.TicketSigningService
            .Setup(s => s.TryVerify(It.IsAny<string>(), out It.Ref<Guid>.IsAny))
            .Returns(false);

        var result = await fixture.CreateHandler().HandleAsync(ticket.Id, "tampered-signature", CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.InvalidTicketSignature);
        ticket.Status.Should().Be(TicketStatus.Issued);
        fixture.TicketRepository.GetForUpdateCallCount.Should().Be(0);
        fixture.UnitOfWork.LastTransaction.Should().BeNull();
    }

    // 對應 AC: TICKET-REDEEM-SIG-EMPTY（空字串／空白字元簽章回傳 InvalidTicketSignature，未呼叫 GetForUpdateAsync）
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task HandleAsync_WhenSignatureIsEmptyOrWhitespace_ReturnsInvalidTicketSignatureAndDoesNotLockTicket(string signature)
    {
        var fixture = new Fixture();
        var ticket = new Ticket(Guid.NewGuid(), Guid.NewGuid(), Now.AddDays(-1));
        fixture.TicketRepository.Data.Add(ticket);
        fixture.TicketSigningService
            .Setup(s => s.TryVerify(It.IsAny<string>(), out It.Ref<Guid>.IsAny))
            .Returns(false);

        var result = await fixture.CreateHandler().HandleAsync(ticket.Id, signature, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.InvalidTicketSignature);
        fixture.TicketRepository.GetForUpdateCallCount.Should().Be(0);
    }
}
