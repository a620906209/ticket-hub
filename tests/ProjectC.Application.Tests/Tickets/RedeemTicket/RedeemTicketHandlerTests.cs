using FluentAssertions;
using Moq;
using ProjectC.Application.Common;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Application.Tickets.RedeemTicket;
using ProjectC.Domain.Orders;
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
        public FakeOrderRepository OrderRepository { get; } = new();
        public Guid OrganizerId { get; } = Guid.NewGuid();

        public RedeemTicketHandler CreateHandler()
            => new(TicketRepository, OrderRepository, UnitOfWork, DateTimeProvider, TicketSigningService.Object);

        /// <summary>建立一張屬於 <paramref name="organizerId"/>（預設為呼叫端 <see cref="OrganizerId"/>）名下活動的票券。</summary>
        public Ticket SeedTicket(Guid? organizerId = null)
        {
            var eventId = Guid.NewGuid();
            var orderItem = new OrderItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 500m);
            OrderRepository.Data.Add(new Order(Guid.NewGuid(), eventId, Guid.NewGuid(), Now.AddMinutes(10), [orderItem]));
            OrderRepository.OrganizerIdByEventId[eventId] = organizerId ?? OrganizerId;

            var ticket = new Ticket(Guid.NewGuid(), orderItem.Id, Now.AddDays(-1));
            TicketRepository.Data.Add(ticket);
            return ticket;
        }
    }

    // 對應 AC: TICKET-REDEEM-SIG-BACKWARD-COMPAT（未提供簽章，成功/404/409 三種既有案例行為不變）
    [Fact]
    public async Task HandleAsync_WhenSignatureNotProvidedAndTicketIsIssued_TransitionsToRedeemedAndRecordsRedeemedAtUtc()
    {
        var fixture = new Fixture();
        var ticket = fixture.SeedTicket();

        var result = await fixture.CreateHandler().HandleAsync(ticket.Id, fixture.OrganizerId, null, CancellationToken.None);

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
        var ticket = fixture.SeedTicket();
        ticket.Redeem(Now.AddHours(-1));
        var redeemedAt = ticket.RedeemedAtUtc;

        var result = await fixture.CreateHandler().HandleAsync(ticket.Id, fixture.OrganizerId, null, CancellationToken.None);

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

        var result = await fixture.CreateHandler().HandleAsync(Guid.NewGuid(), fixture.OrganizerId, null, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
        fixture.UnitOfWork.LastTransaction!.Committed.Should().BeFalse();
    }

    // 對應 AC: TICKET-REDEEM-SIG-VALID（正確簽章成功核銷）
    [Fact]
    public async Task HandleAsync_WhenSignatureValid_RedeemsTicket()
    {
        var fixture = new Fixture();
        var ticket = fixture.SeedTicket();
        fixture.TicketSigningService
            .Setup(s => s.TryVerify(It.IsAny<string>(), out It.Ref<Guid>.IsAny))
            .Returns(true);

        var result = await fixture.CreateHandler().HandleAsync(ticket.Id, fixture.OrganizerId, "valid-signature", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        ticket.Status.Should().Be(TicketStatus.Redeemed);
    }

    // 對應 AC: TICKET-REDEEM-SIG-INVALID（竄改簽章回傳 InvalidTicketSignature，未呼叫 GetForUpdateAsync）
    [Fact]
    public async Task HandleAsync_WhenSignatureInvalid_ReturnsInvalidTicketSignatureAndDoesNotLockTicket()
    {
        var fixture = new Fixture();
        var ticket = fixture.SeedTicket();
        fixture.TicketSigningService
            .Setup(s => s.TryVerify(It.IsAny<string>(), out It.Ref<Guid>.IsAny))
            .Returns(false);

        var result = await fixture.CreateHandler().HandleAsync(ticket.Id, fixture.OrganizerId, "tampered-signature", CancellationToken.None);

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
        var ticket = fixture.SeedTicket();
        fixture.TicketSigningService
            .Setup(s => s.TryVerify(It.IsAny<string>(), out It.Ref<Guid>.IsAny))
            .Returns(false);

        var result = await fixture.CreateHandler().HandleAsync(ticket.Id, fixture.OrganizerId, signature, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.InvalidTicketSignature);
        fixture.TicketRepository.GetForUpdateCallCount.Should().Be(0);
    }

    // 對應 AC: RDM-AUTHZ-004（其他 Organizer 的票券與不存在的票券回傳完全相同的 Error，且不核銷）
    [Fact]
    public async Task HandleAsync_WhenTicketBelongsToOtherOrganizer_ReturnsSameNotFoundErrorAsMissingTicket()
    {
        var fixture = new Fixture();
        var ticket = fixture.SeedTicket(organizerId: Guid.NewGuid());
        var handler = fixture.CreateHandler();

        var otherOrganizerResult = await handler.HandleAsync(ticket.Id, fixture.OrganizerId, null, CancellationToken.None);
        var otherOrganizerTransaction = fixture.UnitOfWork.LastTransaction!;
        fixture.TicketRepository.Data.Clear();
        var missingResult = await handler.HandleAsync(ticket.Id, fixture.OrganizerId, null, CancellationToken.None);

        otherOrganizerResult.IsSuccess.Should().BeFalse();
        otherOrganizerResult.Error.Should().BeEquivalentTo(missingResult.Error);
        otherOrganizerResult.Error!.Type.Should().Be(ErrorType.NotFound);
        ticket.Status.Should().Be(TicketStatus.Issued);
        ticket.RedeemedAtUtc.Should().BeNull();
        otherOrganizerTransaction.Committed.Should().BeFalse();
    }

    // 對應 AC: RDM-AUTHZ-005（其他 Organizer 已核銷的票券 MUST 回 404 而非 409，歸屬核對先於狀態檢查）
    [Fact]
    public async Task HandleAsync_WhenOtherOrganizerTicketAlreadyRedeemed_ReturnsNotFoundNotConflict()
    {
        var fixture = new Fixture();
        var ticket = fixture.SeedTicket(organizerId: Guid.NewGuid());
        ticket.Redeem(Now.AddHours(-1));

        var result = await fixture.CreateHandler().HandleAsync(ticket.Id, fixture.OrganizerId, null, CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.NotFound);
        result.Error.Message.Should().Be($"Ticket '{ticket.Id}' was not found.");
    }

    // 對應 AC: RDM-AUTHZ-007（歸屬查無代表資料毀損，MUST 大聲失敗且交易不 commit、票券不變）
    [Fact]
    public async Task HandleAsync_WhenOrganizerLookupReturnsNull_ThrowsAndDoesNotRedeem()
    {
        var fixture = new Fixture();
        var ticket = fixture.SeedTicket();
        fixture.OrderRepository.OrganizerIdByEventId.Clear();

        var act = () => fixture.CreateHandler().HandleAsync(ticket.Id, fixture.OrganizerId, null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        ticket.Status.Should().Be(TicketStatus.Issued);
        ticket.RedeemedAtUtc.Should().BeNull();
        fixture.UnitOfWork.LastTransaction!.Committed.Should().BeFalse();
    }
}
