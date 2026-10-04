using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ProjectC.Domain.Members;
using ProjectC.Domain.Organizers;
using ProjectC.Domain.Venues;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests.Events;

/// <summary>
/// event-sales-window tasks.md 2.3：實際執行 AddEventSalesWindow 的 Up／Down。結構比照 RealNameVerificationMigrationTests：
/// 每個測試建立自己的獨立資料庫；停在前一支 migration 時目前的 EF model 已含販售期間欄位，所以只能以參數化原生 SQL 讀寫 Events。
/// </summary>
[Collection(PostgresCollection.Name)]
public class EventSalesWindowMigrationTests
{
    private const string MigrationBeforeSalesWindow = "20261002022252_AddRealNameVerification";
    private const string SalesWindowMigration = "20261003115627_AddEventSalesWindow";

    private readonly PostgresFixture _fixture;

    public EventSalesWindowMigrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record LegacyEventRow(Guid Id, string Title, DateTime StartAtUtc, Guid VenueId, Guid SeatMapId, Guid OrganizerId, bool IsQueueModeEnabled, bool IsRealNameRequired);

    private sealed record SalesWindowRow(DateTime? SalesStartAtUtc, DateTime? SalesEndAtUtc);

    private async Task<ApplicationDbContext> CreateDatabaseAsync()
    {
        var databaseName = $"migration_{Guid.NewGuid():N}";
        await using (var serverContext = _fixture.CreateDbContext())
        {
            // 資料庫名稱是測試自己產生的 GUID，不含外部輸入；CREATE DATABASE 不支援參數化識別字。
            await serverContext.Database.ExecuteSqlRawAsync($"CREATE DATABASE \"{databaseName}\"");
        }

        var connectionString = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = databaseName }.ConnectionString;
        return new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connectionString).Options);
    }

    private static Task MigrateToAsync(ApplicationDbContext dbContext, string? targetMigration = null)
        => dbContext.GetService<IMigrator>().MigrateAsync(targetMigration);

    /// <summary>Members／Organizer／Venue／SeatMap 的 schema 在兩支 migration 間沒有變化，可直接以 EF 寫入；Events 以 SQL 寫入。</summary>
    private static async Task<LegacyEventRow> SeedLegacyEventAsync(ApplicationDbContext dbContext)
    {
        var owner = Member.Register($"legacy-{Guid.NewGuid():N}@example.com", "Legacy Member", "hash");
        dbContext.Members.Add(owner);
        var organizer = Organizer.Apply(Guid.NewGuid(), "Legacy Organizer", owner.Id, DateTime.UtcNow);
        organizer.Approve(owner.Id, DateTime.UtcNow);
        var venue = new Venue(Guid.NewGuid(), "Legacy Venue");
        var seatMap = new SeatMap(Guid.NewGuid(), venue.Id);
        dbContext.Organizers.Add(organizer);
        dbContext.Venues.Add(venue);
        dbContext.SeatMaps.Add(seatMap);
        await dbContext.SaveChangesAsync();

        var row = new LegacyEventRow(Guid.NewGuid(), "Legacy Event", DateTime.UtcNow.Date.AddDays(30), venue.Id, seatMap.Id, organizer.Id, true, true);
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Events" ("Id", "Title", "StartAtUtc", "VenueId", "SeatMapId", "OrganizerId", "IsQueueModeEnabled", "IsRealNameRequired")
            VALUES ({row.Id}, {row.Title}, {row.StartAtUtc}, {row.VenueId}, {row.SeatMapId}, {row.OrganizerId}, {row.IsQueueModeEnabled}, {row.IsRealNameRequired})
            """);
        return row;
    }

    private static Task<LegacyEventRow> ReadLegacyColumnsAsync(ApplicationDbContext dbContext, Guid eventId)
        => dbContext.Database.SqlQuery<LegacyEventRow>($"""
            SELECT "Id", "Title", "StartAtUtc", "VenueId", "SeatMapId", "OrganizerId", "IsQueueModeEnabled", "IsRealNameRequired"
            FROM "Events" WHERE "Id" = {eventId}
            """).SingleAsync();

    private static Task<SalesWindowRow> ReadSalesWindowAsync(ApplicationDbContext dbContext, Guid eventId)
        => dbContext.Database.SqlQuery<SalesWindowRow>($"""
            SELECT "SalesStartAtUtc", "SalesEndAtUtc" FROM "Events" WHERE "Id" = {eventId}
            """).SingleAsync();

    private static Task<List<string>> GetSalesWindowColumnNamesAsync(ApplicationDbContext dbContext)
        => dbContext.Database.SqlQuery<string>($"""
            SELECT column_name AS "Value" FROM information_schema.columns
            WHERE table_name = 'Events' AND column_name IN ('SalesStartAtUtc', 'SalesEndAtUtc')
            """).ToListAsync();

    private static Task<bool> IsSalesWindowMigrationAppliedAsync(ApplicationDbContext dbContext)
        => dbContext.Database.SqlQuery<int>($"""
            SELECT 1 AS "Value" FROM "__EFMigrationsHistory" WHERE "MigrationId" = {SalesWindowMigration}
            """).AnyAsync();

    private static Task<bool> EventExistsAsync(ApplicationDbContext dbContext, Guid eventId)
        => dbContext.Database.SqlQuery<int>($"""SELECT 1 AS "Value" FROM "Events" WHERE "Id" = {eventId}""").AnyAsync();

    // EVT-SALES-010：既有活動遷移後不回填，兩欄位皆為 null，其他欄位不受影響。
    [Fact]
    public async Task Up_WhenLegacyEventExists_LeavesSalesWindowNull()
    {
        await using var dbContext = await CreateDatabaseAsync();
        await MigrateToAsync(dbContext, MigrationBeforeSalesWindow);
        var inserted = await SeedLegacyEventAsync(dbContext);

        await MigrateToAsync(dbContext, SalesWindowMigration);

        (await ReadSalesWindowAsync(dbContext, inserted.Id)).Should().Be(new SalesWindowRow(null, null));
        (await ReadLegacyColumnsAsync(dbContext, inserted.Id)).Should().Be(inserted);
    }

    // EVT-SALES-011：只設定開賣時間——Down 必須中止，否則販售期間被靜默丟棄、重新升版後活動變成立即可售。
    [Fact]
    public async Task Down_WhenSalesStartIsSet_ThrowsAndKeepsColumnsAndData()
    {
        await using var dbContext = await CreateDatabaseAsync();
        await MigrateToAsync(dbContext);
        var salesStartAtUtc = DateTime.UtcNow.Date.AddDays(1);
        var (eventId, _) = await TicketingTestData.SeedEventWithSeatsAsync(dbContext, seatCount: 1, salesStartAtUtc: salesStartAtUtc);

        var act = () => MigrateToAsync(dbContext, MigrationBeforeSalesWindow);

        await act.Should().ThrowAsync<PostgresException>();
        (await GetSalesWindowColumnNamesAsync(dbContext)).Should().BeEquivalentTo("SalesStartAtUtc", "SalesEndAtUtc");
        (await ReadSalesWindowAsync(dbContext, eventId)).Should().Be(new SalesWindowRow(salesStartAtUtc, null));
        (await IsSalesWindowMigrationAppliedAsync(dbContext)).Should().BeTrue();
    }

    // EVT-SALES-011：只設定停售時間——與上一案分開，防護條件若只檢查 SalesStartAtUtc，只有本案會失敗。
    [Fact]
    public async Task Down_WhenSalesEndIsSet_ThrowsAndKeepsColumnsAndData()
    {
        await using var dbContext = await CreateDatabaseAsync();
        await MigrateToAsync(dbContext);
        var salesEndAtUtc = DateTime.UtcNow.Date.AddDays(20);
        var (eventId, _) = await TicketingTestData.SeedEventWithSeatsAsync(dbContext, seatCount: 1, salesEndAtUtc: salesEndAtUtc);

        var act = () => MigrateToAsync(dbContext, MigrationBeforeSalesWindow);

        await act.Should().ThrowAsync<PostgresException>();
        (await GetSalesWindowColumnNamesAsync(dbContext)).Should().BeEquivalentTo("SalesStartAtUtc", "SalesEndAtUtc");
        (await ReadSalesWindowAsync(dbContext, eventId)).Should().Be(new SalesWindowRow(null, salesEndAtUtc));
        (await IsSalesWindowMigrationAppliedAsync(dbContext)).Should().BeTrue();
    }

    // EVT-SALES-012：沒有任何販售期間資料時 Down 可正常移除欄位，活動本身保留。
    [Fact]
    public async Task Down_WhenNoSalesWindowData_RemovesColumns()
    {
        await using var dbContext = await CreateDatabaseAsync();
        await MigrateToAsync(dbContext);
        var (eventId, _) = await TicketingTestData.SeedEventWithSeatsAsync(dbContext, seatCount: 1);

        await MigrateToAsync(dbContext, MigrationBeforeSalesWindow);

        (await GetSalesWindowColumnNamesAsync(dbContext)).Should().BeEmpty();
        (await EventExistsAsync(dbContext, eventId)).Should().BeTrue();
        (await IsSalesWindowMigrationAppliedAsync(dbContext)).Should().BeFalse();
    }
}
