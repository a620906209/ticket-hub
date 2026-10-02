using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProjectC.Domain.Members;
using ProjectC.Domain.Orders;
using ProjectC.Infrastructure.Persistence.Repositories;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests.Orders;

/// <summary>核銷的「需實名」閘門與查詢持票人都依賴這個投影；欄位取錯（例如拿到別的活動的設定或錯的買家）
/// 會讓實名活動被當成不需實名核銷，或顯示別人的實名（real-name-verification design.md 決策 4）。</summary>
[Collection(PostgresCollection.Name)]
public class GetRedemptionContextByOrderItemIdAsyncTests
{
    private readonly PostgresFixture _fixture;

    public GetRedemptionContextByOrderItemIdAsyncTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetRedemptionContextByOrderItemIdAsync_WhenOrderItemExists_ReturnsOrganizerRealNameFlagAndBuyer(bool isRealNameRequired)
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var (eventId, _) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 1, isRealNameRequired: isRealNameRequired);
        var ticketTypeId = await TicketingTestData.SeedCountBasedTicketTypeAsync(seedDbContext, eventId);
        var organizerId = await seedDbContext.Events.AsNoTracking().Where(e => e.Id == eventId).Select(e => e.OrganizerId).SingleAsync();
        var buyer = Member.Register($"buyer-{Guid.NewGuid():N}@example.com", "Buyer", "hash");
        var orderItem = new OrderItem(Guid.NewGuid(), ticketTypeId, null, 1, 300m);
        seedDbContext.Members.Add(buyer);
        seedDbContext.Orders.Add(new Order(Guid.NewGuid(), eventId, buyer.Id, DateTime.UtcNow.AddMinutes(10), [orderItem]));
        await seedDbContext.SaveChangesAsync();

        await using var readDbContext = _fixture.CreateDbContext();
        var context = await new OrderRepository(readDbContext).GetRedemptionContextByOrderItemIdAsync(orderItem.Id, CancellationToken.None);

        context.Should().Be(new RedemptionContext(organizerId, isRealNameRequired, buyer.Id));
    }

    [Fact]
    public async Task GetRedemptionContextByOrderItemIdAsync_WhenOrderItemNotFound_ReturnsNull()
    {
        await using var readDbContext = _fixture.CreateDbContext();

        var context = await new OrderRepository(readDbContext).GetRedemptionContextByOrderItemIdAsync(Guid.NewGuid(), CancellationToken.None);

        context.Should().BeNull();
    }
}
