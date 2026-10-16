using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ProjectC.Domain.Members;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests.Orders;

/// <summary>
/// admin-order-list-enrichment tasks.md 3.4：實際執行 AddOrderCreatedAt 的 Up／Down。結構比照 EventSalesWindowMigrationTests：
/// 每個測試建立自己的獨立資料庫；停在前一支 migration 時目前的 EF model 已含 CreatedAtUtc，所以只能以參數化原生 SQL 讀寫 Orders。
/// </summary>
[Collection(PostgresCollection.Name)]
public class OrderCreatedAtBackfillMigrationTests
{
    private const string MigrationBeforeCreatedAt = "20261003115627_AddEventSalesWindow";
    private const string CreatedAtMigration = "20261014081305_AddOrderCreatedAt";

    private readonly PostgresFixture _fixture;

    public OrderCreatedAtBackfillMigrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record OrderCreatedAtRow(Guid Id, DateTime CreatedAtUtc);

    private async Task<ApplicationDbContext> CreateDatabaseAsync()
    {
        var databaseName = $"migration_{Guid.NewGuid():N}";
        await using (var serverContext = _fixture.CreateDbContext())
        {
            // 資料庫名稱是測試自己產生的 GUID，不含外部輸入；CREATE DATABASE 不支援參數化識別字。
            #pragma warning disable EF1002 // 理由同上方註解
            await serverContext.Database.ExecuteSqlRawAsync($"CREATE DATABASE \"{databaseName}\"");
            #pragma warning restore EF1002
        }

        var connectionString = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = databaseName }.ConnectionString;
        return new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connectionString).Options);
    }

    private static Task MigrateToAsync(ApplicationDbContext dbContext, string? targetMigration = null)
        => dbContext.GetService<IMigrator>().MigrateAsync(targetMigration);

    /// <summary>Events／Members 的 schema 在兩支 migration 間沒有變化，可直接以 EF 寫入；Orders 以 SQL 寫入（不含 CreatedAtUtc）。</summary>
    private static async Task<(Guid EventId, Guid BuyerId)> SeedEventAndBuyerAsync(ApplicationDbContext dbContext)
    {
        var (eventId, _) = await TicketingTestData.SeedEventWithSeatsAsync(dbContext, seatCount: 1);
        var buyer = Member.Register($"legacy-buyer-{Guid.NewGuid():N}@example.com", "Legacy Buyer", "hash");
        dbContext.Members.Add(buyer);
        await dbContext.SaveChangesAsync();
        return (eventId, buyer.Id);
    }

    private static async Task<Guid> InsertLegacyOrderAsync(ApplicationDbContext dbContext, Guid eventId, Guid buyerId, DateTime heldUntilUtc)
    {
        var orderId = Guid.NewGuid();
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "Orders" ("Id", "EventId", "BuyerId", "HeldUntilUtc", "Status") VALUES ({orderId}, {eventId}, {buyerId}, {heldUntilUtc}, 1)""");
        return orderId;
    }

    private static Task<List<OrderCreatedAtRow>> ReadCreatedAtAsync(ApplicationDbContext dbContext)
        => dbContext.Database.SqlQuery<OrderCreatedAtRow>($"""SELECT "Id", "CreatedAtUtc" FROM "Orders" ORDER BY "Id" """).ToListAsync();

    private static Task<bool> CreatedAtColumnExistsAsync(ApplicationDbContext dbContext)
        => dbContext.Database.SqlQuery<int>($"""
            SELECT 1 AS "Value" FROM information_schema.columns
            WHERE table_name = 'Orders' AND column_name = 'CreatedAtUtc'
            """).AnyAsync();

    private static Task<int> CountOrdersAsync(ApplicationDbContext dbContext)
        => dbContext.Database.SqlQuery<int>($"""SELECT COUNT(*)::int AS "Value" FROM "Orders" """).SingleAsync();

    // TO-CREATED-003：兩筆 HeldUntilUtc 不同的既有訂單各自回填為自己的 HeldUntilUtc − 10 分鐘——
    // 以單一常數（例如 now()）回填會讓兩筆值相同而失敗；之後 CreatedAtUtc 不允許 NULL。
    [Fact]
    public async Task Up_WhenLegacyOrdersExist_BackfillsEachFromOwnHeldUntilAndMakesColumnNotNull()
    {
        await using var dbContext = await CreateDatabaseAsync();
        await MigrateToAsync(dbContext, MigrationBeforeCreatedAt);
        var (eventId, buyerId) = await SeedEventAndBuyerAsync(dbContext);
        var heldUntilA = new DateTime(2026, 3, 1, 8, 30, 0, DateTimeKind.Utc);
        var heldUntilB = new DateTime(2026, 9, 15, 20, 5, 0, DateTimeKind.Utc);
        var orderA = await InsertLegacyOrderAsync(dbContext, eventId, buyerId, heldUntilA);
        var orderB = await InsertLegacyOrderAsync(dbContext, eventId, buyerId, heldUntilB);

        await MigrateToAsync(dbContext, CreatedAtMigration);

        (await ReadCreatedAtAsync(dbContext)).Should().BeEquivalentTo(
        [
            new OrderCreatedAtRow(orderA, heldUntilA.AddMinutes(-10)),
            new OrderCreatedAtRow(orderB, heldUntilB.AddMinutes(-10)),
        ]);

        var insertWithoutCreatedAt = () => InsertLegacyOrderAsync(dbContext, eventId, buyerId, heldUntilA);
        (await insertWithoutCreatedAt.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.NotNullViolation);
    }

    // design.md 決策 2：Down 只刪欄、不刪訂單；再 Up 由 HeldUntilUtc 重新推導，得到與第一次 Up 相同的值。
    [Fact]
    public async Task DownThenUp_KeepsOrdersAndRestoresSameCreatedAt()
    {
        await using var dbContext = await CreateDatabaseAsync();
        await MigrateToAsync(dbContext, MigrationBeforeCreatedAt);
        var (eventId, buyerId) = await SeedEventAndBuyerAsync(dbContext);
        await InsertLegacyOrderAsync(dbContext, eventId, buyerId, new DateTime(2026, 3, 1, 8, 30, 0, DateTimeKind.Utc));
        await InsertLegacyOrderAsync(dbContext, eventId, buyerId, new DateTime(2026, 9, 15, 20, 5, 0, DateTimeKind.Utc));
        await MigrateToAsync(dbContext, CreatedAtMigration);
        var afterFirstUp = await ReadCreatedAtAsync(dbContext);

        await MigrateToAsync(dbContext, MigrationBeforeCreatedAt);

        (await CreatedAtColumnExistsAsync(dbContext)).Should().BeFalse();
        (await CountOrdersAsync(dbContext)).Should().Be(2);

        await MigrateToAsync(dbContext, CreatedAtMigration);

        (await ReadCreatedAtAsync(dbContext)).Should().Equal(afterFirstUp);
    }
}
