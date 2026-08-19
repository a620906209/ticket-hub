using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProjectC.Application.Orders.GetOrderById;
using ProjectC.Domain.Members;
using ProjectC.Infrastructure.Persistence.Repositories;
using ProjectC.Infrastructure.Security;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests;

/// <summary>驗證 OrderItem 的 private EF Core 物化建構子真的能被正確綁定——直接用 raw SQL 植入一筆
/// TicketTypeId IS NULL 的既有座位訂單（模擬 migration 前建立、不回填的歷史資料，不透過應用程式正常
/// 流程建立，正常流程一定會帶 TicketTypeId），驗證的是 EF Core 具現化路徑本身，不是單純測 DTO mapping
/// （design.md 決策 2，外部審查第五輪抓到的阻斷問題，見 tasks.md 8.4）。</summary>
[Collection(PostgresCollection.Name)]
public class GetOrderByIdLegacyDataTests
{
    private readonly PostgresFixture _fixture;

    public GetOrderByIdLegacyDataTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task HandleAsync_WhenOrderItemHasNullTicketTypeId_MaterializesSuccessfullyAndReturnsNullTicketTypeId()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var (eventId, eventSeatIds) = await TicketingTestData.SeedEventWithSeatsAsync(seedDbContext, seatCount: 1);
        var buyer = Member.Register($"buyer-{Guid.NewGuid():N}@example.com", "Test Buyer", "hash");
        seedDbContext.Members.Add(buyer);
        await seedDbContext.SaveChangesAsync();

        var orderId = Guid.NewGuid();
        var heldUntilUtc = DateTime.UtcNow.AddMinutes(10);
        await seedDbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "Orders" ("Id", "EventId", "BuyerId", "HeldUntilUtc", "Status") VALUES ({orderId}, {eventId}, {buyer.Id}, {heldUntilUtc}, 0)""");
        // 刻意不指定 "TicketTypeId"（欄位維持 NULL）與 "Quantity"（套用 DB 端 DEFAULT 1），
        // 模擬 migration 前既有座位訂單、不回填的既有資料形狀。
        await seedDbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "OrderItems" ("Id", "EventSeatId", "UnitPrice", "OrderId") VALUES ({Guid.NewGuid()}, {eventSeatIds[0]}, 500, {orderId})""");

        await using var readDbContext = _fixture.CreateDbContext();
        var handler = new GetOrderByIdHandler(new OrderRepository(readDbContext), new SystemDateTimeProvider());

        var result = await handler.HandleAsync(orderId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue("private EF 物化建構子須能正確綁定 TicketTypeId IS NULL 的舊列，不能查詢失敗");
        var item = result.Value!.Items.Single();
        item.TicketTypeId.Should().BeNull();
        item.EventSeatId.Should().Be(eventSeatIds[0]);
        item.Quantity.Should().Be(1);
        item.UnitPrice.Should().Be(500m);
    }
}
