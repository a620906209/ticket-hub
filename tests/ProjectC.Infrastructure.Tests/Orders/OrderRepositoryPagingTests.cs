using FluentAssertions;
using ProjectC.Domain.Members;
using ProjectC.Domain.Orders;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Repositories;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests.Orders;

/// <summary>list-pagination：分頁、排序、總數必須在資料庫端完成，且 <c>TotalCount</c> 與 <c>Items</c> 使用同一個授權範圍條件。</summary>
[Collection(PostgresCollection.Name)]
public class OrderRepositoryPagingTests
{
    private static readonly DateTime BaseTime = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _fixture;

    public OrderRepositoryPagingTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record Scope(Guid OrganizerId, Guid EventId, Guid TicketTypeId);

    private static async Task<Scope> SeedScopeAsync(ApplicationDbContext dbContext)
    {
        var (eventId, _) = await TicketingTestData.SeedEventWithSeatsAsync(dbContext, seatCount: 1);
        var ticketTypeId = await TicketingTestData.SeedCountBasedTicketTypeAsync(dbContext, eventId);
        var organizerId = dbContext.Events.Where(e => e.Id == eventId).Select(e => e.OrganizerId).Single();
        return new Scope(organizerId, eventId, ticketTypeId);
    }

    private static Guid AddBuyer(ApplicationDbContext dbContext)
    {
        var buyer = Member.Register($"buyer-{Guid.NewGuid():N}@example.com", "Test Buyer", "hash");
        dbContext.Members.Add(buyer);
        return buyer.Id;
    }

    private static Order AddOrder(ApplicationDbContext dbContext, Scope scope, Guid buyerId, DateTime createdAtUtc, Guid? id = null, int itemCount = 1)
    {
        var items = Enumerable.Range(0, itemCount)
            .Select(_ => new OrderItem(Guid.NewGuid(), scope.TicketTypeId, null, 1, 300m))
            .ToList();
        var order = new Order(id ?? Guid.NewGuid(), scope.EventId, buyerId, createdAtUtc, createdAtUtc.AddMinutes(10), items);
        dbContext.Orders.Add(order);
        return order;
    }

    // 對應 AC: BOQ-LIST-PAGE-001 / LP-RESP-001（買家：切頁與只計入自己的訂單）
    [Fact]
    public async Task GetPageByBuyerIdAsync_WhenOtherBuyersHaveOrders_PagesAndCountsOnlyCallerOrders()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var scope = await SeedScopeAsync(seedDbContext);
        var buyerId = AddBuyer(seedDbContext);
        var otherBuyerId = AddBuyer(seedDbContext);
        var mine = Enumerable.Range(0, 5).Select(i => AddOrder(seedDbContext, scope, buyerId, BaseTime.AddMinutes(i)).Id).ToList();
        var others = Enumerable.Range(0, 3).Select(i => AddOrder(seedDbContext, scope, otherBuyerId, BaseTime.AddMinutes(i)).Id).ToList();
        await seedDbContext.SaveChangesAsync();

        await using var readDbContext = _fixture.CreateDbContext();
        var page = await new OrderRepository(readDbContext).GetPageByBuyerIdAsync(buyerId, skip: 2, take: 2, CancellationToken.None);

        page.TotalCount.Should().Be(5);
        // 新到舊：mine[4], mine[3] | mine[2], mine[1] | mine[0]；skip 2 take 2 = 第二頁。
        page.Items.Select(o => o.Id).Should().Equal(mine[2], mine[1]);
        page.Items.Select(o => o.Id).Should().NotIntersectWith(others);
    }

    // 對應 AC: BOQ-LIST-PAGE-001 / ORD-PAGE-001（後台：TotalCount 只計入該 Organizer 名下活動的訂單）
    [Fact]
    public async Task GetPageByOrganizerIdAsync_WhenOtherOrganizerHasOrders_PagesAndCountsOnlyCallerOrganizerOrders()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var scopeA = await SeedScopeAsync(seedDbContext);
        var scopeB = await SeedScopeAsync(seedDbContext);
        var buyerId = AddBuyer(seedDbContext);
        var ordersA = Enumerable.Range(0, 5).Select(i => AddOrder(seedDbContext, scopeA, buyerId, BaseTime.AddMinutes(i)).Id).ToList();
        var ordersB = Enumerable.Range(0, 4).Select(i => AddOrder(seedDbContext, scopeB, buyerId, BaseTime.AddMinutes(i)).Id).ToList();
        await seedDbContext.SaveChangesAsync();

        await using var readDbContext = _fixture.CreateDbContext();
        var page = await new OrderRepository(readDbContext).GetPageByOrganizerIdAsync(scopeA.OrganizerId, skip: 2, take: 2, CancellationToken.None);

        page.TotalCount.Should().Be(5);
        page.Items.Select(o => o.Id).Should().Equal(ordersA[2], ordersA[1]);
        page.Items.Select(o => o.Id).Should().NotIntersectWith(ordersB);
    }

    private static readonly int[] OffsetsInInsertOrder = [3, 1, 4, 0, 2];
    private static readonly int[] OffsetsNewestFirst = [4, 3, 2, 1, 0];

    // 對應 AC: BOQ-LIST-ORDER-001（買家：寫入順序打亂，結果仍新到舊）
    [Fact]
    public async Task GetPageByBuyerIdAsync_WhenInsertedOutOfOrder_ReturnsNewestFirst()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var scope = await SeedScopeAsync(seedDbContext);
        var buyerId = AddBuyer(seedDbContext);
        var idByOffset = OffsetsInInsertOrder.ToDictionary(offset => offset, offset => AddOrder(seedDbContext, scope, buyerId, BaseTime.AddMinutes(offset)).Id);
        await seedDbContext.SaveChangesAsync();

        await using var readDbContext = _fixture.CreateDbContext();
        var page = await new OrderRepository(readDbContext).GetPageByBuyerIdAsync(buyerId, skip: 0, take: 10, CancellationToken.None);

        page.Items.Select(o => o.Id).Should().Equal(OffsetsNewestFirst.Select(offset => idByOffset[offset]));
    }

    // 對應 AC: ORD-LIST-006（後台：寫入順序打亂，結果仍新到舊；誤用升冪必須失敗）
    [Fact]
    public async Task GetPageByOrganizerIdAsync_WhenInsertedOutOfOrder_ReturnsNewestFirst()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var scope = await SeedScopeAsync(seedDbContext);
        var buyerId = AddBuyer(seedDbContext);
        var idByOffset = OffsetsInInsertOrder.ToDictionary(offset => offset, offset => AddOrder(seedDbContext, scope, buyerId, BaseTime.AddMinutes(offset)).Id);
        await seedDbContext.SaveChangesAsync();

        await using var readDbContext = _fixture.CreateDbContext();
        var page = await new OrderRepository(readDbContext).GetPageByOrganizerIdAsync(scope.OrganizerId, skip: 0, take: 10, CancellationToken.None);

        page.Items.Select(o => o.Id).Should().Equal(OffsetsNewestFirst.Select(offset => idByOffset[offset]));
    }

    // 對應 AC: BOQ-LIST-ORDER-002 / LP-ORDER-001（買家：建立時間相同依 Id 升冪，逐頁合併每筆恰好一次）
    [Fact]
    public async Task GetPageByBuyerIdAsync_WhenCreatedAtTies_OrdersByIdAscendingAndPagesWithoutOverlap()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var scope = await SeedScopeAsync(seedDbContext);
        var buyerId = AddBuyer(seedDbContext);
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
        foreach (var id in ids.AsEnumerable().Reverse())
        {
            AddOrder(seedDbContext, scope, buyerId, BaseTime, id);
        }
        await seedDbContext.SaveChangesAsync();

        await using var readDbContext = _fixture.CreateDbContext();
        var repository = new OrderRepository(readDbContext);
        var collected = new List<Guid>();
        for (var skip = 0; skip < 5; skip += 2)
        {
            var page = await repository.GetPageByBuyerIdAsync(buyerId, skip, take: 2, CancellationToken.None);
            collected.AddRange(page.Items.Select(o => o.Id));
        }

        collected.Should().Equal(ids.OrderBy(id => id));
    }

    // 對應 AC: ORD-PAGE-002 / ORD-LIST-008 / LP-ORDER-001（後台：同上）
    [Fact]
    public async Task GetPageByOrganizerIdAsync_WhenCreatedAtTies_OrdersByIdAscendingAndPagesWithoutOverlap()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var scope = await SeedScopeAsync(seedDbContext);
        var buyerId = AddBuyer(seedDbContext);
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
        foreach (var id in ids.AsEnumerable().Reverse())
        {
            AddOrder(seedDbContext, scope, buyerId, BaseTime, id);
        }
        await seedDbContext.SaveChangesAsync();

        await using var readDbContext = _fixture.CreateDbContext();
        var repository = new OrderRepository(readDbContext);
        var collected = new List<Guid>();
        for (var skip = 0; skip < 5; skip += 2)
        {
            var page = await repository.GetPageByOrganizerIdAsync(scope.OrganizerId, skip, take: 2, CancellationToken.None);
            collected.AddRange(page.Items.Select(o => o.Id));
        }

        collected.Should().Equal(ids.OrderBy(id => id));
    }

    // 對應 AC: LP-RESP-003 / LP-RESP-004（超過最後一頁回空 Items 與實際總數；無資料回空與 0）
    [Fact]
    public async Task GetPageAsync_WhenSkipBeyondTotalOrNoData_ReturnsEmptyItemsWithActualTotal()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var scope = await SeedScopeAsync(seedDbContext);
        var buyerId = AddBuyer(seedDbContext);
        var emptyBuyerId = AddBuyer(seedDbContext);
        for (var i = 0; i < 3; i++)
        {
            AddOrder(seedDbContext, scope, buyerId, BaseTime.AddMinutes(i));
        }
        await seedDbContext.SaveChangesAsync();

        await using var readDbContext = _fixture.CreateDbContext();
        var repository = new OrderRepository(readDbContext);

        var buyerBeyond = await repository.GetPageByBuyerIdAsync(buyerId, skip: 60, take: 20, CancellationToken.None);
        var organizerBeyond = await repository.GetPageByOrganizerIdAsync(scope.OrganizerId, skip: 60, take: 20, CancellationToken.None);
        var buyerEmpty = await repository.GetPageByBuyerIdAsync(emptyBuyerId, skip: 0, take: 20, CancellationToken.None);
        var organizerEmpty = await repository.GetPageByOrganizerIdAsync(Guid.NewGuid(), skip: 0, take: 20, CancellationToken.None);

        buyerBeyond.Items.Should().BeEmpty();
        buyerBeyond.TotalCount.Should().Be(3);
        organizerBeyond.Items.Should().BeEmpty();
        organizerBeyond.TotalCount.Should().Be(3);
        buyerEmpty.Items.Should().BeEmpty();
        buyerEmpty.TotalCount.Should().Be(0);
        organizerEmpty.Items.Should().BeEmpty();
        organizerEmpty.TotalCount.Should().Be(0);
    }

    // 對應 AC: 訂單明細必須隨當頁一併載入（Handler 計算張數與金額依賴 Items）
    [Fact]
    public async Task GetPageAsync_LoadsOrderItemsForEachOrderOnThePage()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var scope = await SeedScopeAsync(seedDbContext);
        var buyerId = AddBuyer(seedDbContext);
        var itemCountByOrderId = new Dictionary<Guid, int>();
        for (var i = 0; i < 3; i++)
        {
            var order = AddOrder(seedDbContext, scope, buyerId, BaseTime.AddMinutes(i), itemCount: i + 1);
            itemCountByOrderId[order.Id] = i + 1;
        }
        await seedDbContext.SaveChangesAsync();

        await using var readDbContext = _fixture.CreateDbContext();
        var repository = new OrderRepository(readDbContext);
        var buyerPage = await repository.GetPageByBuyerIdAsync(buyerId, skip: 0, take: 3, CancellationToken.None);
        var organizerPage = await repository.GetPageByOrganizerIdAsync(scope.OrganizerId, skip: 0, take: 3, CancellationToken.None);

        buyerPage.Items.Should().HaveCount(3).And.AllSatisfy(o => o.Items.Should().HaveCount(itemCountByOrderId[o.Id]));
        organizerPage.Items.Should().HaveCount(3).And.AllSatisfy(o => o.Items.Should().HaveCount(itemCountByOrderId[o.Id]));
    }
}
