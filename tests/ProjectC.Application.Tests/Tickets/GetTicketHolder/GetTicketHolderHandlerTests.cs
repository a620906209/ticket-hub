using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectC.Application.Common;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Application.Tickets.GetTicketHolder;
using ProjectC.Domain.Members;
using ProjectC.Domain.Orders;
using ProjectC.Domain.Tickets;

namespace ProjectC.Application.Tests.Tickets.GetTicketHolder;

/// <summary>持票人端點是唯一會回傳完整實名與末四碼的地方，暴露範圍必須限縮在「本 Organizer、需實名、尚未核銷」的票券
/// （real-name-verification RDM-HOLDER-*）。</summary>
public class GetTicketHolderHandlerTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly MemberRealName HolderRealName = new("王小明", "0912");

    private sealed class Fixture
    {
        public FakeTicketRepository TicketRepository { get; } = new();
        public FakeOrderRepository OrderRepository { get; } = new();
        public FakeMemberRealNameRepository MemberRealNameRepository { get; } = new();
        public Guid OrganizerId { get; } = Guid.NewGuid();
        public Guid CallerMemberId { get; } = Guid.NewGuid();

        public GetTicketHolderHandler CreateHandler() => new(
            TicketRepository, OrderRepository, MemberRealNameRepository, NullLogger<GetTicketHolderHandler>.Instance);

        public Ticket SeedTicket(bool isRealNameRequired, Guid? organizerId = null, bool isBuyerRegistered = true)
        {
            var eventId = Guid.NewGuid();
            var buyerId = Guid.NewGuid();
            var orderItem = new OrderItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 500m);
            OrderRepository.Data.Add(new Order(Guid.NewGuid(), eventId, buyerId, Now.AddMinutes(10), [orderItem]));
            OrderRepository.OrganizerIdByEventId[eventId] = organizerId ?? OrganizerId;
            if (isRealNameRequired) OrderRepository.RealNameRequiredEventIds.Add(eventId);
            if (isBuyerRegistered) MemberRealNameRepository.Data[buyerId] = HolderRealName;

            var ticket = new Ticket(Guid.NewGuid(), orderItem.Id, Now.AddDays(-1));
            TicketRepository.Data.Add(ticket);
            return ticket;
        }

        public Task<Result<TicketHolderDto>> HandleAsync(Guid ticketId, CancellationToken cancellationToken = default)
            => CreateHandler().HandleAsync(ticketId, OrganizerId, CallerMemberId, cancellationToken);
    }

    // RDM-HOLDER-001：現場比對證件需要完整末四碼，這裡刻意不遮蔽（以前導 0 確認未被當數字處理）。
    [Fact]
    public async Task HandleAsync_WhenRealNameRequired_ReturnsFullRealNameAndNationalIdLast4()
    {
        var fixture = new Fixture();
        var ticket = fixture.SeedTicket(isRealNameRequired: true);

        var result = await fixture.HandleAsync(ticket.Id);

        result.Value.Should().Be(new TicketHolderDto(ticket.Id, "Issued", true, "王小明", "0912"));
    }

    // RDM-HOLDER-002：不需實名的活動不得因為買家剛好有登記就外洩實名。
    [Fact]
    public async Task HandleAsync_WhenRealNameNotRequiredAndBuyerRegistered_ReturnsNullHolderFieldsWithoutQueryingRealName()
    {
        var fixture = new Fixture();
        var ticket = fixture.SeedTicket(isRealNameRequired: false);

        var result = await fixture.HandleAsync(ticket.Id);

        result.Value.Should().Be(new TicketHolderDto(ticket.Id, "Issued", false, null, null));
        fixture.MemberRealNameRepository.GetCallCount.Should().Be(0);
    }

    // RDM-HOLDER-003：其他 Organizer 的票與不存在的票回應完全相同，且在讀取實名之前就擋下。
    [Fact]
    public async Task HandleAsync_WhenTicketBelongsToOtherOrganizer_ReturnsSameNotFoundAsMissingTicketWithoutQueryingRealName()
    {
        var fixture = new Fixture();
        var ticket = fixture.SeedTicket(isRealNameRequired: true, organizerId: Guid.NewGuid());

        var otherOrganizerResult = await fixture.HandleAsync(ticket.Id);
        fixture.TicketRepository.Data.Clear();
        var missingResult = await fixture.HandleAsync(ticket.Id);

        otherOrganizerResult.Error!.Type.Should().Be(ErrorType.NotFound);
        otherOrganizerResult.Error.Should().BeEquivalentTo(missingResult.Error);
        fixture.MemberRealNameRepository.GetCallCount.Should().Be(0);
    }

    // RDM-HOLDER-006：查詢持票人是核銷前的確認步驟，不得順手改變票券狀態或加鎖。
    [Fact]
    public async Task HandleAsync_WhenSucceeded_DoesNotChangeTicketStateOrLockTicket()
    {
        var fixture = new Fixture();
        var ticket = fixture.SeedTicket(isRealNameRequired: true);

        await fixture.HandleAsync(ticket.Id);

        ticket.Status.Should().Be(TicketStatus.Issued);
        ticket.RedeemedAtUtc.Should().BeNull();
        fixture.TicketRepository.GetForUpdateCallCount.Should().Be(0);
    }

    // RDM-HOLDER-007：需實名卻查不到實名代表資料損毀，必須大聲失敗；例外訊息會進日誌，不得含個資。
    [Fact]
    public async Task HandleAsync_WhenRealNameRequiredButBuyerHasNoRealName_ThrowsWithoutPersonalData()
    {
        var fixture = new Fixture();
        var ticket = fixture.SeedTicket(isRealNameRequired: true, isBuyerRegistered: false);
        fixture.MemberRealNameRepository.Data[Guid.NewGuid()] = HolderRealName;

        var act = () => fixture.HandleAsync(ticket.Id);

        var exception = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        exception.Message.Should().Contain(ticket.Id.ToString());
        HexIdentifierText.RemoveHexIdentifiers(exception.Message).Should().NotContain(HolderRealName.RealName)
            .And.NotContain(HolderRealName.NationalIdLast4);
    }

    // RDM-HOLDER-008：核銷後即不可再讀實名，縮小個資暴露窗口。
    [Fact]
    public async Task HandleAsync_WhenTicketRedeemed_ReturnsConflictWithoutQueryingRealName()
    {
        var fixture = new Fixture();
        var ticket = fixture.SeedTicket(isRealNameRequired: true);
        ticket.Redeem(Now.AddHours(-1));

        var result = await fixture.HandleAsync(ticket.Id);

        result.Error!.Type.Should().Be(ErrorType.Conflict);
        fixture.MemberRealNameRepository.GetCallCount.Should().Be(0);
    }

    // RDM-HOLDER-009：活動開始後仍是入場核銷的主要時段，不得以開演時間限制查詢；
    // handler 刻意不注入時間來源、RedemptionContext 也不含開演時間，單元層只能確認 Issued 票不受其他條件阻擋；
    // 真正「開演時間已過」的資料由 5.11 整合測試以實際活動資料驗證。
    [Fact]
    public async Task HandleAsync_WhenEventAlreadyStartedAndTicketIssued_StillReturnsHolder()
    {
        var fixture = new Fixture();
        var ticket = fixture.SeedTicket(isRealNameRequired: true);

        var result = await fixture.HandleAsync(ticket.Id);

        result.IsSuccess.Should().BeTrue();
        result.Value!.HolderRealName.Should().Be("王小明");
    }

    [Fact]
    public async Task HandleAsync_WhenOrganizerCannotBeResolved_Throws()
    {
        var fixture = new Fixture();
        var ticket = fixture.SeedTicket(isRealNameRequired: true);
        fixture.OrderRepository.OrganizerIdByEventId.Clear();

        var act = () => fixture.HandleAsync(ticket.Id);

        await act.Should().ThrowAsync<InvalidOperationException>();
        fixture.MemberRealNameRepository.GetCallCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_ForwardsCancellationTokenToRepositories()
    {
        var fixture = new Fixture();
        var ticket = fixture.SeedTicket(isRealNameRequired: true);
        using var cancellationTokenSource = new CancellationTokenSource();

        await fixture.HandleAsync(ticket.Id, cancellationTokenSource.Token);

        fixture.OrderRepository.LastGetRedemptionContextToken.Should().Be(cancellationTokenSource.Token);
        fixture.MemberRealNameRepository.LastGetToken.Should().Be(cancellationTokenSource.Token);
    }
}
