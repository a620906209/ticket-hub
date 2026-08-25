using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProjectC.Application.Orders;
using ProjectC.Application.Orders.PlaceOrder;
using ProjectC.Domain.Members;
using ProjectC.Domain.Orders;
using ProjectC.Domain.Tickets;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests;

/// <summary>驗證純計數（不綁座位）票種在真實並發下不超賣，以及混合多計數票種的鎖定順序不會死鎖
/// （design.md 決策 3，外部審查第四輪抓到的兩個阻斷問題）。跟 OrderServiceConcurrencyTests 一樣，
/// 用兩個獨立的 DbContext/OrderService instance 模擬兩個真實請求——單一 DbContext 內的測試
/// 測不出 EF Core identity resolution 造成的鎖前舊快照問題。</summary>
[Collection(PostgresCollection.Name)]
public class TicketTypeConcurrencyTests
{
    private readonly PostgresFixture _fixture;

    public TicketTypeConcurrencyTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static OrderService CreateOrderService(ApplicationDbContext dbContext, ProjectC.Domain.Payments.IPaymentGateway? paymentGateway = null)
        => OrderServiceTestFactory.Create(dbContext, paymentGateway ?? new ThreadSafeFakePaymentGateway());

    private async Task<Guid> SeedCountBasedTicketTypeAsync(ApplicationDbContext dbContext, Guid eventId, string zoneCode, int availableQuantity)
    {
        var @event = await dbContext.Events.AsNoTracking().SingleAsync(e => e.Id == eventId);
        var ticketType = @event.CreateCountBasedTicketType(zoneCode, 300m, availableQuantity);
        dbContext.TicketTypes.Add(ticketType);
        await dbContext.SaveChangesAsync();
        return ticketType.Id;
    }

    private async Task<Guid> SeedEventAsync(ApplicationDbContext dbContext)
    {
        var (eventId, _) = await TicketingTestData.SeedEventWithSeatsAsync(dbContext, seatCount: 0);
        return eventId;
    }

    private async Task<Guid> SeedBuyerAsync(ApplicationDbContext dbContext)
    {
        var buyer = Member.Register($"buyer-{Guid.NewGuid():N}@example.com", "Test Buyer", "hash");
        dbContext.Members.Add(buyer);
        await dbContext.SaveChangesAsync();
        return buyer.Id;
    }

    [Fact]
    public async Task PlaceOrderAsync_TwoConcurrentRequestsBuyingLastUnit_OnlyOneSucceedsAndFinalQuantityIsZero()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var eventId = await SeedEventAsync(seedDbContext);
        var ticketTypeId = await SeedCountBasedTicketTypeAsync(seedDbContext, eventId, "站票", availableQuantity: 1);
        var buyerAId = await SeedBuyerAsync(seedDbContext);
        var buyerBId = await SeedBuyerAsync(seedDbContext);

        await using var dbContextA = _fixture.CreateDbContext();
        await using var dbContextB = _fixture.CreateDbContext();
        var serviceA = CreateOrderService(dbContextA);
        var serviceB = CreateOrderService(dbContextB);
        var requestA = new PlaceOrderRequest([new PlaceOrderSelectionRequest(null, ticketTypeId, 1)]);
        var requestB = new PlaceOrderRequest([new PlaceOrderSelectionRequest(null, ticketTypeId, 1)]);

        var taskA = serviceA.PlaceOrderAsync(buyerAId, requestA, CancellationToken.None);
        var taskB = serviceB.PlaceOrderAsync(buyerBId, requestB, CancellationToken.None);
        var results = await Task.WhenAll(taskA, taskB);

        results.Count(r => r.IsSuccess).Should().Be(1,
            "只剩 1 張庫存、兩個並發請求各買 1 張，只能有一個成功——這是驗證 AsNoTracking 修正是否真正生效的唯一方式");

        await using var readDbContext = _fixture.CreateDbContext();
        var reloadedTicketType = await readDbContext.TicketTypes.AsNoTracking().SingleAsync(t => t.Id == ticketTypeId);
        reloadedTicketType.AvailableQuantity.Should().Be(0);
    }

    [Fact]
    public async Task PlaceOrderAsync_TwoConcurrentOrdersEachBuyingFromTwoDifferentCountingTicketTypes_DoesNotDeadlock()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var eventId = await SeedEventAsync(seedDbContext);
        var standingTicketTypeId = await SeedCountBasedTicketTypeAsync(seedDbContext, eventId, "站票", availableQuantity: 10);
        var parkingTicketTypeId = await SeedCountBasedTicketTypeAsync(seedDbContext, eventId, "停車票", availableQuantity: 10);
        var buyerAId = await SeedBuyerAsync(seedDbContext);
        var buyerBId = await SeedBuyerAsync(seedDbContext);

        await using var dbContextA = _fixture.CreateDbContext();
        await using var dbContextB = _fixture.CreateDbContext();
        var serviceA = CreateOrderService(dbContextA);
        var serviceB = CreateOrderService(dbContextB);
        // 兩筆訂單都同時買兩個票種，GetForUpdateAsync 內部依 Id 排序鎖定，不論選購項目在請求裡的順序，
        // 兩個交易鎖定順序永遠一致，理論上不該死鎖（design.md 決策 3）。
        var requestA = new PlaceOrderRequest([
            new PlaceOrderSelectionRequest(null, standingTicketTypeId, 1),
            new PlaceOrderSelectionRequest(null, parkingTicketTypeId, 1)
        ]);
        var requestB = new PlaceOrderRequest([
            new PlaceOrderSelectionRequest(null, parkingTicketTypeId, 1),
            new PlaceOrderSelectionRequest(null, standingTicketTypeId, 1)
        ]);

        var taskA = serviceA.PlaceOrderAsync(buyerAId, requestA, CancellationToken.None);
        var taskB = serviceB.PlaceOrderAsync(buyerBId, requestB, CancellationToken.None);
        var bothCompleted = Task.WhenAll(taskA, taskB);
        var winner = await Task.WhenAny(bothCompleted, Task.Delay(TimeSpan.FromSeconds(15)));

        winner.Should().BeSameAs(bothCompleted, "兩筆訂單應該都在合理時間內完成，不應因鎖定順序不一致而死鎖/逾時");
        var results = await bothCompleted;
        results.Should().OnlyContain(r => r.IsSuccess, "兩個票種庫存都充足，兩筆訂單應該都成功");
    }

    [Fact]
    public async Task ConfirmOrderAsync_TwoConcurrentConfirmsOnSamePureCountingOrder_OnlyOneSucceedsAndPaymentChargedOnce()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var eventId = await SeedEventAsync(seedDbContext);
        var ticketTypeId = await SeedCountBasedTicketTypeAsync(seedDbContext, eventId, "站票", availableQuantity: 10);
        var buyerId = await SeedBuyerAsync(seedDbContext);

        var seedService = CreateOrderService(seedDbContext);
        var placeResult = await seedService.PlaceOrderAsync(
            buyerId, new PlaceOrderRequest([new PlaceOrderSelectionRequest(null, ticketTypeId, 3)]), CancellationToken.None);
        placeResult.IsSuccess.Should().BeTrue();
        var orderId = placeResult.Value;

        var sharedPaymentGateway = new ThreadSafeFakePaymentGateway();
        await using var dbContextA = _fixture.CreateDbContext();
        await using var dbContextB = _fixture.CreateDbContext();
        var serviceA = CreateOrderService(dbContextA, sharedPaymentGateway);
        var serviceB = CreateOrderService(dbContextB, sharedPaymentGateway);

        var taskA = serviceA.ConfirmOrderAsync(orderId, buyerId, CancellationToken.None);
        var taskB = serviceB.ConfirmOrderAsync(orderId, buyerId, CancellationToken.None);
        var results = await Task.WhenAll(taskA, taskB);

        results.Count(r => r.IsSuccess).Should().Be(1, "純計數訂單沒有座位可鎖，鎖 TicketType 是唯一的序列化點，兩個並發確認只能有一個成功");
        sharedPaymentGateway.CallCount.Should().Be(1, "MUST NOT 觸發第二次付款");
    }
}
