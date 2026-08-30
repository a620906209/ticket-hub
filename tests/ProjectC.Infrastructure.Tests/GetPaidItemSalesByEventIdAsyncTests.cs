using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProjectC.Domain.Members;
using ProjectC.Domain.Orders;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Repositories;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests;

/// <summary>驗證 sales-report 能力的 <see cref="OrderRepository.GetPaidItemSalesByEventIdAsync"/>
/// 資料庫端分組彙總（design.md 決策 1）。</summary>
[Collection(PostgresCollection.Name)]
public class GetPaidItemSalesByEventIdAsyncTests
{
    private readonly PostgresFixture _fixture;

    public GetPaidItemSalesByEventIdAsyncTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static async Task<Guid> SeedBuyerAsync(ApplicationDbContext dbContext, CancellationToken ct = default)
    {
        var buyer = Member.Register($"buyer-{Guid.NewGuid():N}@example.com", "Test Buyer", "hash");
        dbContext.Members.Add(buyer);
        await dbContext.SaveChangesAsync(ct);
        return buyer.Id;
    }

    [Fact]
    public async Task GetPaidItemSalesByEventIdAsync_WhenMixedSeatAndCountTicketTypesArePaid_ReturnsCorrectGroupsPerTicketType()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var (eventId, eventSeatIds) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 1);
        var seatTicketTypeId = await TicketingTestData.SeedTicketTypeAsync(seedDbContext, eventId, price: 500m);
        var countTicketTypeId = await TicketingTestData.SeedCountBasedTicketTypeAsync(seedDbContext, eventId, price: 300m, availableQuantity: 100);
        var buyerId = await SeedBuyerAsync(seedDbContext);

        var order = new Order(Guid.NewGuid(), eventId, buyerId, DateTime.UtcNow.AddMinutes(10),
        [
            new OrderItem(Guid.NewGuid(), seatTicketTypeId, eventSeatIds[0], quantity: 1, unitPrice: 500m),
            new OrderItem(Guid.NewGuid(), countTicketTypeId, eventSeatId: null, quantity: 3, unitPrice: 300m),
        ]);
        order.Confirm();
        seedDbContext.Orders.Add(order);
        await seedDbContext.SaveChangesAsync();

        await using var readDbContext = _fixture.CreateDbContext();
        var repository = new OrderRepository(readDbContext);

        var groups = await repository.GetPaidItemSalesByEventIdAsync(eventId, CancellationToken.None);

        groups.Should().HaveCount(2);
        var seatGroup = groups.Single(g => g.TicketTypeId == seatTicketTypeId);
        seatGroup.ItemCount.Should().Be(1);
        seatGroup.QuantitySold.Should().Be(1);
        seatGroup.Revenue.Should().Be(500m);
        var countGroup = groups.Single(g => g.TicketTypeId == countTicketTypeId);
        countGroup.ItemCount.Should().Be(1);
        countGroup.QuantitySold.Should().Be(3);
        countGroup.Revenue.Should().Be(900m);
    }

    [Fact]
    public async Task GetPaidItemSalesByEventIdAsync_WhenOrdersArePendingOrCancelled_ExcludesThemFromGroups()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var (eventId, _) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 0);
        var ticketTypeId = await TicketingTestData.SeedCountBasedTicketTypeAsync(seedDbContext, eventId);
        var buyerId = await SeedBuyerAsync(seedDbContext);

        var paidOrder = new Order(Guid.NewGuid(), eventId, buyerId, DateTime.UtcNow.AddMinutes(10),
            [new OrderItem(Guid.NewGuid(), ticketTypeId, eventSeatId: null, quantity: 5, unitPrice: 300m)]);
        paidOrder.Confirm();

        var pendingOrder = new Order(Guid.NewGuid(), eventId, buyerId, DateTime.UtcNow.AddMinutes(10),
            [new OrderItem(Guid.NewGuid(), ticketTypeId, eventSeatId: null, quantity: 2, unitPrice: 300m)]);

        var cancelledOrder = new Order(Guid.NewGuid(), eventId, buyerId, DateTime.UtcNow.AddMinutes(10),
            [new OrderItem(Guid.NewGuid(), ticketTypeId, eventSeatId: null, quantity: 1, unitPrice: 300m)]);
        cancelledOrder.Cancel();

        seedDbContext.Orders.AddRange(paidOrder, pendingOrder, cancelledOrder);
        await seedDbContext.SaveChangesAsync();

        await using var readDbContext = _fixture.CreateDbContext();
        var repository = new OrderRepository(readDbContext);

        var groups = await repository.GetPaidItemSalesByEventIdAsync(eventId, CancellationToken.None);

        groups.Should().ContainSingle();
        var group = groups.Single();
        group.TicketTypeId.Should().Be(ticketTypeId);
        group.ItemCount.Should().Be(1);
        group.QuantitySold.Should().Be(5);
        group.Revenue.Should().Be(1500m);
    }

    [Fact]
    public async Task GetPaidItemSalesByEventIdAsync_WhenPaidItemHasNullTicketTypeId_ReturnsSeparateUnclassifiedGroup()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var (eventId, eventSeatIds) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 1);
        var buyer = Member.Register($"buyer-{Guid.NewGuid():N}@example.com", "Test Buyer", "hash");
        seedDbContext.Members.Add(buyer);
        await seedDbContext.SaveChangesAsync();

        var orderId = Guid.NewGuid();
        var heldUntilUtc = DateTime.UtcNow.AddMinutes(10);
        // Status = 1 (Paid)，模擬 migration 前既有座位訂單、TicketTypeId 未回填的舊資料形狀
        // （見 GetOrderByIdLegacyDataTests 同樣的植入手法）。
        await seedDbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "Orders" ("Id", "EventId", "BuyerId", "HeldUntilUtc", "Status") VALUES ({orderId}, {eventId}, {buyer.Id}, {heldUntilUtc}, 1)""");
        await seedDbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "OrderItems" ("Id", "EventSeatId", "UnitPrice", "OrderId") VALUES ({Guid.NewGuid()}, {eventSeatIds[0]}, 500, {orderId})""");

        await using var readDbContext = _fixture.CreateDbContext();
        var repository = new OrderRepository(readDbContext);

        var groups = await repository.GetPaidItemSalesByEventIdAsync(eventId, CancellationToken.None);

        groups.Should().ContainSingle();
        var group = groups.Single();
        group.TicketTypeId.Should().BeNull();
        group.ItemCount.Should().Be(1);
        group.QuantitySold.Should().Be(1);
        group.Revenue.Should().Be(500m);
    }

    [Fact]
    public async Task GetPaidItemSalesByEventIdAsync_WhenEventHasNoPaidOrders_ReturnsEmptyList()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var (eventId, _) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 0);

        await using var readDbContext = _fixture.CreateDbContext();
        var repository = new OrderRepository(readDbContext);

        var groups = await repository.GetPaidItemSalesByEventIdAsync(eventId, CancellationToken.None);

        groups.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPaidItemSalesByEventIdAsync_WhenTicketTypeBelongsToAnotherEvent_StillReturnsTheGroup()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var (eventAId, _) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 0);
        var (eventBId, _) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 0);
        var otherEventTicketTypeId = await TicketingTestData.SeedCountBasedTicketTypeAsync(seedDbContext, eventBId, price: 300m);
        var buyerId = await SeedBuyerAsync(seedDbContext);

        // 資料異常情境：Order.EventId = Event-A，但其項目的 TicketTypeId 指向屬於 Event-B 的 TicketType
        // ——正常下單流程（OrderService.PlaceOrderAsync 的跨活動驗證）不會產生這種組合，這裡直接繞過該流程
        // 用 Domain 建構子＋DbContext 存檔，驗證查詢本身（不判斷票種所屬活動，見 design.md 決策 1 契約）
        // 真的能撈出這個分組，這個判斷責任在 Application 層（design.md 決策 2、3）。
        var order = new Order(Guid.NewGuid(), eventAId, buyerId, DateTime.UtcNow.AddMinutes(10),
            [new OrderItem(Guid.NewGuid(), otherEventTicketTypeId, eventSeatId: null, quantity: 2, unitPrice: 300m)]);
        order.Confirm();
        seedDbContext.Orders.Add(order);
        await seedDbContext.SaveChangesAsync();

        await using var readDbContext = _fixture.CreateDbContext();
        var repository = new OrderRepository(readDbContext);

        var groups = await repository.GetPaidItemSalesByEventIdAsync(eventAId, CancellationToken.None);

        groups.Should().ContainSingle();
        var group = groups.Single();
        group.TicketTypeId.Should().Be(otherEventTicketTypeId);
        group.ItemCount.Should().Be(1);
        group.QuantitySold.Should().Be(2);
        group.Revenue.Should().Be(600m);
    }
}
