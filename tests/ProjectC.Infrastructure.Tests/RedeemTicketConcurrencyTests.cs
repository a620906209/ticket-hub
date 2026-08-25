using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProjectC.Application.Orders;
using ProjectC.Application.Orders.PlaceOrder;
using ProjectC.Application.Tickets.RedeemTicket;
using ProjectC.Domain.Members;
using ProjectC.Domain.Tickets;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Repositories;
using ProjectC.Infrastructure.Security;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests;

// 驗證 ticket-redemption spec「並發核銷同一張票」：兩個並發請求核銷同一張 Issued Ticket，只能有一個成功。
// 比照 OrderServiceConcurrencyTests 的並發測試手法，用兩個獨立的 DbContext instance 模擬兩個真實請求。
[Collection(PostgresCollection.Name)]
public class RedeemTicketConcurrencyTests
{
    private readonly PostgresFixture _fixture;

    public RedeemTicketConcurrencyTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static OrderService CreateOrderService(ApplicationDbContext dbContext)
        => OrderServiceTestFactory.Create(dbContext);

    private static RedeemTicketHandler CreateRedeemTicketHandler(ApplicationDbContext dbContext)
        => new(new TicketRepository(dbContext), new UnitOfWork(dbContext), new SystemDateTimeProvider());

    // 走真實的「下單 → 確認付款」流程觸發出票（比照 AdminTicketsControllerTests 的種子資料手法），
    // 確保建立出來的 Ticket 具備真實、通過所有驗證規則的 OrderItemId 外鍵。
    private async Task<Guid> SeedIssuedTicketAsync(ApplicationDbContext dbContext)
    {
        var (eventId, eventSeatIds) = await TicketingTestData.SeedEventWithSeatsAsync(dbContext, seatCount: 1);
        var ticketTypeId = await TicketingTestData.SeedTicketTypeAsync(dbContext, eventId);

        var buyer = Member.Register($"buyer-{Guid.NewGuid():N}@example.com", "Test Buyer", "hash");
        dbContext.Members.Add(buyer);
        await dbContext.SaveChangesAsync();

        var orderService = CreateOrderService(dbContext);
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeatIds[0], ticketTypeId)]);
        var placeResult = await orderService.PlaceOrderAsync(buyer.Id, request, CancellationToken.None);
        placeResult.IsSuccess.Should().BeTrue();

        var confirmResult = await orderService.ConfirmOrderAsync(placeResult.Value, buyer.Id, CancellationToken.None);
        confirmResult.IsSuccess.Should().BeTrue();

        // 這個 Postgres 容器由整個測試 collection 共用（見 PostgresCollection），其他測試也會留下自己的
        // Ticket 資料，不能假設整張表只有一列——限定在這筆訂單自己的 OrderItemId 範圍內查詢。
        var orderItemIds = await dbContext.OrderItems.AsNoTracking()
            .Where(i => EF.Property<Guid>(i, "OrderId") == placeResult.Value)
            .Select(i => i.Id)
            .ToListAsync();
        var ticket = await dbContext.Tickets.AsNoTracking().SingleAsync(t => orderItemIds.Contains(t.OrderItemId));
        return ticket.Id;
    }

    [Fact]
    public async Task HandleAsync_TwoConcurrentRedeemsOnSameIssuedTicket_OnlyOneSucceedsAndTheOtherIsRejected()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var ticketId = await SeedIssuedTicketAsync(seedDbContext);

        await using var dbContextA = _fixture.CreateDbContext();
        await using var dbContextB = _fixture.CreateDbContext();
        var handlerA = CreateRedeemTicketHandler(dbContextA);
        var handlerB = CreateRedeemTicketHandler(dbContextB);

        var taskA = handlerA.HandleAsync(ticketId, CancellationToken.None);
        var taskB = handlerB.HandleAsync(ticketId, CancellationToken.None);
        var results = await Task.WhenAll(taskA, taskB);

        results.Count(r => r.IsSuccess).Should().Be(1, "兩個並發核銷同一張票，只能有一個成功，另一個 MUST 被拒絕而非誤報成功");

        await using var readDbContext = _fixture.CreateDbContext();
        var reloadedTicket = await readDbContext.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId);
        reloadedTicket.Status.Should().Be(TicketStatus.Redeemed);
    }
}
