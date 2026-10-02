using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ProjectC.Domain.Members;
using ProjectC.Domain.Organizers;
using ProjectC.Domain.Venues;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Repositories;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests.Members;

/// <summary>
/// real-name-verification tasks.md 2.8：實際執行 AddRealNameVerification 的 Up／Down。
/// 每個測試在同一個 Postgres 容器內建立自己的獨立資料庫。停在前一支 migration 時，目前的 EF model
/// 已含實名欄位，所以 Members／Events 只能以參數化原生 SQL 寫入（同 EventOrganizerBackfillMigrationTests 的做法）。
/// </summary>
[Collection(PostgresCollection.Name)]
public class RealNameVerificationMigrationTests
{
    private const string MigrationBeforeRealName = "20260923090100_EnforceEventOrganizerIdNotNull";

    private readonly PostgresFixture _fixture;

    public RealNameVerificationMigrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

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

    private static async Task<Guid> SeedLegacyMemberAsync(ApplicationDbContext dbContext)
    {
        var memberId = Guid.NewGuid();
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Members" ("Id", "Email", "DisplayName", "PasswordHash", "Role", "IsActive")
            VALUES ({memberId}, {$"legacy-{memberId:N}@example.com"}, 'Legacy Member', 'hash', 0, true)
            """);
        return memberId;
    }

    /// <summary>Organizer／Venue／SeatMap 的 schema 在兩支 migration 間沒有變化，可直接以 EF 寫入。</summary>
    private static async Task<Guid> SeedLegacyEventAsync(ApplicationDbContext dbContext)
    {
        var ownerId = await SeedLegacyMemberAsync(dbContext);
        var organizer = Organizer.Apply(Guid.NewGuid(), "Legacy Organizer", ownerId, DateTime.UtcNow);
        organizer.Approve(ownerId, DateTime.UtcNow);
        var venue = new Venue(Guid.NewGuid(), "Legacy Venue");
        var seatMap = new SeatMap(Guid.NewGuid(), venue.Id);
        dbContext.Organizers.Add(organizer);
        dbContext.Venues.Add(venue);
        dbContext.SeatMaps.Add(seatMap);
        await dbContext.SaveChangesAsync();

        var eventId = Guid.NewGuid();
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Events" ("Id", "Title", "StartAtUtc", "VenueId", "SeatMapId", "OrganizerId", "IsQueueModeEnabled")
            VALUES ({eventId}, 'Legacy Event', {DateTime.UtcNow.AddDays(30)}, {venue.Id}, {seatMap.Id}, {organizer.Id}, false)
            """);
        return eventId;
    }

    /// <summary>在最新 schema 下以 EF 建立會員與活動，供 Down 測試使用。</summary>
    private static async Task<(Guid MemberId, Guid EventId)> SeedCurrentDataAsync(ApplicationDbContext dbContext, bool isRealNameRegistered, bool isRealNameRequired)
    {
        var (eventId, _) = await TicketingTestData.SeedEventWithSeatsAsync(dbContext, seatCount: 1, isRealNameRequired: isRealNameRequired);
        var member = Member.Register($"member-{Guid.NewGuid():N}@example.com", "Member", "hash");
        dbContext.Members.Add(member);
        await dbContext.SaveChangesAsync();
        if (isRealNameRegistered)
            await new MemberRealNameRepository(dbContext).TryRegisterAsync(member.Id, "王小明", "1234", CancellationToken.None);

        return (member.Id, eventId);
    }

    private static Task<List<string>> GetRealNameColumnNamesAsync(ApplicationDbContext dbContext)
        => dbContext.Database.SqlQuery<string>($"""
            SELECT column_name AS "Value" FROM information_schema.columns
            WHERE (table_name = 'Members' AND column_name IN ('RealName', 'NationalIdLast4'))
               OR (table_name = 'Events' AND column_name = 'IsRealNameRequired')
            """).ToListAsync();

    private static Task<bool> RealNameCheckConstraintExistsAsync(ApplicationDbContext dbContext)
        => dbContext.Database.SqlQuery<int>($"""
            SELECT 1 AS "Value" FROM information_schema.table_constraints
            WHERE table_name = 'Members' AND constraint_name = 'CK_Members_RealName_NationalIdLast4'
            """).AnyAsync();

    // EVT-REALNAME-004：既有活動升版後一律為不需實名，不得因回填而突然擋下既有買家。
    [Fact]
    public async Task Up_WhenLegacyEventExists_BackfillsIsRealNameRequiredFalse()
    {
        await using var dbContext = await CreateDatabaseAsync();
        await MigrateToAsync(dbContext, MigrationBeforeRealName);
        var eventId = await SeedLegacyEventAsync(dbContext);

        await MigrateToAsync(dbContext);

        var @event = await dbContext.Events.AsNoTracking().SingleAsync(e => e.Id == eventId);
        @event.IsRealNameRequired.Should().BeFalse();
    }

    // RNV-ROLLBACK-001：已有會員登記實名時，Down 必須中止；否則實名會被靜默刪除，重新升版後變成未登記、可改登記他人實名。
    [Fact]
    public async Task Down_WhenMemberHasRegisteredRealName_ThrowsAndKeepsColumnsAndData()
    {
        await using var dbContext = await CreateDatabaseAsync();
        await MigrateToAsync(dbContext);
        var (memberId, eventId) = await SeedCurrentDataAsync(dbContext, isRealNameRegistered: true, isRealNameRequired: false);

        var act = () => MigrateToAsync(dbContext, MigrationBeforeRealName);

        await act.Should().ThrowAsync<PostgresException>();
        (await GetRealNameColumnNamesAsync(dbContext)).Should().BeEquivalentTo("RealName", "NationalIdLast4", "IsRealNameRequired");
        var member = await dbContext.Members.AsNoTracking().SingleAsync(m => m.Id == memberId);
        (member.RealName, member.NationalIdLast4).Should().Be(("王小明", "1234"));
        (await dbContext.Events.AsNoTracking().SingleAsync(e => e.Id == eventId)).IsRealNameRequired.Should().BeFalse();
    }

    // RNV-ROLLBACK-002：只有需實名活動也必須中止；否則重新升版後該活動變成不需實名，未登記者可直接購票。
    [Fact]
    public async Task Down_WhenRealNameRequiredEventExists_ThrowsAndKeepsColumnsAndData()
    {
        await using var dbContext = await CreateDatabaseAsync();
        await MigrateToAsync(dbContext);
        var (_, eventId) = await SeedCurrentDataAsync(dbContext, isRealNameRegistered: false, isRealNameRequired: true);

        var act = () => MigrateToAsync(dbContext, MigrationBeforeRealName);

        await act.Should().ThrowAsync<PostgresException>();
        (await GetRealNameColumnNamesAsync(dbContext)).Should().BeEquivalentTo("RealName", "NationalIdLast4", "IsRealNameRequired");
        (await RealNameCheckConstraintExistsAsync(dbContext)).Should().BeTrue();
        (await dbContext.Events.AsNoTracking().SingleAsync(e => e.Id == eventId)).IsRealNameRequired.Should().BeTrue();
    }

    // RNV-ROLLBACK-003：沒有實名資料時 Down 可正常移除 schema，再升版後回到全部未登記／不需實名。
    [Fact]
    public async Task Down_WhenNoRealNameData_RemovesSchemaAndUpRestoresDefaults()
    {
        await using var dbContext = await CreateDatabaseAsync();
        await MigrateToAsync(dbContext);
        var (memberId, eventId) = await SeedCurrentDataAsync(dbContext, isRealNameRegistered: false, isRealNameRequired: false);

        await MigrateToAsync(dbContext, MigrationBeforeRealName);

        (await GetRealNameColumnNamesAsync(dbContext)).Should().BeEmpty();
        (await RealNameCheckConstraintExistsAsync(dbContext)).Should().BeFalse();

        await MigrateToAsync(dbContext);

        (await dbContext.Members.AsNoTracking().AnyAsync(m => m.RealName != null || m.NationalIdLast4 != null)).Should().BeFalse();
        (await dbContext.Events.AsNoTracking().AnyAsync(e => e.IsRealNameRequired)).Should().BeFalse();
        (await dbContext.Members.AsNoTracking().AnyAsync(m => m.Id == memberId)).Should().BeTrue();
        (await dbContext.Events.AsNoTracking().AnyAsync(e => e.Id == eventId)).Should().BeTrue();
    }
}
