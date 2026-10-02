using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProjectC.Domain.Members;
using ProjectC.Domain.Orders;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Repositories;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests.Orders;

/// <summary>驗證 <see cref="OrderRepository"/> 依 Organizer 歸屬查詢的資料庫端行為
/// （order-report-redemption-organizer-scoping design.md Decision 1）。</summary>
[Collection(PostgresCollection.Name)]
public class OrderRepositoryOrganizerScopingTests
{
    private readonly PostgresFixture _fixture;

    public OrderRepositoryOrganizerScopingTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record SeededOrganizerOrders(Guid OrganizerId, IReadOnlyList<Guid> OrderIds, IReadOnlyList<Guid> OrderItemIds, IReadOnlyDictionary<Guid, int> ItemCountByOrderId);

    // 每個 Organizer 都是新建的，共用容器裡其他測試留下的訂單不會落在這個 Organizer 名下，
    // 所以斷言可以要求「完全等於」而不必排除雜訊。
    private static async Task<SeededOrganizerOrders> SeedOrganizerWithOrdersAsync(ApplicationDbContext dbContext, int orderCount)
    {
        var (eventId, _) = await TicketingTestData.SeedEventWithSeatsAsync(dbContext, seatCount: 1);
        var ticketTypeId = await TicketingTestData.SeedCountBasedTicketTypeAsync(dbContext, eventId);
        var organizerId = await dbContext.Events.AsNoTracking().Where(e => e.Id == eventId).Select(e => e.OrganizerId).SingleAsync();

        var buyer = Member.Register($"buyer-{Guid.NewGuid():N}@example.com", "Test Buyer", "hash");
        dbContext.Members.Add(buyer);

        var orderIds = new List<Guid>();
        var orderItemIds = new List<Guid>();
        var itemCountByOrderId = new Dictionary<Guid, int>();
        for (var i = 0; i < orderCount; i++)
        {
            // 每筆訂單的明細數量不同（1、2、…），讓 Include 是否完整載入可以逐筆驗證。
            var items = Enumerable.Range(0, i + 1)
                .Select(_ => new OrderItem(Guid.NewGuid(), ticketTypeId, null, 1, 300m))
                .ToList();
            var order = new Order(Guid.NewGuid(), eventId, buyer.Id, DateTime.UtcNow.AddMinutes(10), items);
            dbContext.Orders.Add(order);

            orderIds.Add(order.Id);
            orderItemIds.AddRange(items.Select(item => item.Id));
            itemCountByOrderId[order.Id] = items.Count;
        }

        await dbContext.SaveChangesAsync();
        return new SeededOrganizerOrders(organizerId, orderIds, orderItemIds, itemCountByOrderId);
    }

    private ApplicationDbContext CreateDbContextWithRecorder(MaterializedEntityRecorder recorder)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .AddInterceptors(recorder)
            .Options;
        return new ApplicationDbContext(options);
    }

    // 對應 AC: ORD-LIST-001
    [Fact]
    public async Task GetByOrganizerIdAsync_WhenOrdersExistUnderTwoOrganizers_ReturnsOnlyCallerOrganizerOrdersWithItems()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var organizerA = await SeedOrganizerWithOrdersAsync(seedDbContext, orderCount: 2);
        var organizerB = await SeedOrganizerWithOrdersAsync(seedDbContext, orderCount: 2);

        await using var readDbContext = _fixture.CreateDbContext();
        var orders = await new OrderRepository(readDbContext).GetByOrganizerIdAsync(organizerA.OrganizerId, CancellationToken.None);

        orders.Select(o => o.Id).Should().BeEquivalentTo(organizerA.OrderIds);
        orders.Select(o => o.Id).Should().NotIntersectWith(organizerB.OrderIds);
        orders.Should().AllSatisfy(o => o.Items.Should().HaveCount(organizerA.ItemCountByOrderId[o.Id]));
    }

    // 對應 AC: ORD-LIST-001（資料庫端過濾：其他 Organizer 的訂單與明細不得被具現化進記憶體）
    [Fact]
    public async Task GetByOrganizerIdAsync_DoesNotMaterializeOtherOrganizerOrders()
    {
        // seed 與查詢用不同 DbContext，避免 seed 時已追蹤的實體讓 EF 略過具現化而漏記。
        await using var seedDbContext = _fixture.CreateDbContext();
        var organizerA = await SeedOrganizerWithOrdersAsync(seedDbContext, orderCount: 2);
        var organizerB = await SeedOrganizerWithOrdersAsync(seedDbContext, orderCount: 2);

        var recorder = new MaterializedEntityRecorder();
        await using var readDbContext = CreateDbContextWithRecorder(recorder);
        var orders = await new OrderRepository(readDbContext).GetByOrganizerIdAsync(organizerA.OrganizerId, CancellationToken.None);

        recorder.Orders.Select(o => o.Id).Should().OnlyContain(id => organizerA.OrderIds.Contains(id));
        recorder.Orders.Select(o => o.Id).Should().NotIntersectWith(organizerB.OrderIds);
        recorder.OrderItems.Select(i => i.Id).Should().OnlyContain(id => organizerA.OrderItemIds.Contains(id));
        recorder.OrderItems.Select(i => i.Id).Should().NotIntersectWith(organizerB.OrderItemIds);
        // 紀錄器確實生效：避免 interceptor 沒掛上時，空集合讓上面的斷言空洞通過。
        recorder.Orders.Should().HaveCount(organizerA.OrderIds.Count);

        orders.Select(o => o.Id).Should().BeEquivalentTo(organizerA.OrderIds);
        orders.Should().AllSatisfy(o => o.Items.Should().HaveCount(organizerA.ItemCountByOrderId[o.Id]));
    }

    // 對應 AC: RDM-AUTHZ-004（歸屬查詢回傳票券所屬活動的 OrganizerId）
    [Fact]
    public async Task GetRedemptionContextByOrderItemIdAsync_WhenOrderItemExists_ReturnsEventOrganizerId()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var organizerA = await SeedOrganizerWithOrdersAsync(seedDbContext, orderCount: 1);
        await SeedOrganizerWithOrdersAsync(seedDbContext, orderCount: 1);

        await using var readDbContext = _fixture.CreateDbContext();
        var context = await new OrderRepository(readDbContext).GetRedemptionContextByOrderItemIdAsync(organizerA.OrderItemIds[0], CancellationToken.None);

        context!.OrganizerId.Should().Be(organizerA.OrganizerId);
    }

    // 對應 AC: RDM-AUTHZ-007（查無時回 null，由 Handler 大聲失敗）
    [Fact]
    public async Task GetRedemptionContextByOrderItemIdAsync_WhenOrderItemNotFound_ReturnsNull()
    {
        await using var readDbContext = _fixture.CreateDbContext();

        var context = await new OrderRepository(readDbContext).GetRedemptionContextByOrderItemIdAsync(Guid.NewGuid(), CancellationToken.None);

        context.Should().BeNull();
    }
}
