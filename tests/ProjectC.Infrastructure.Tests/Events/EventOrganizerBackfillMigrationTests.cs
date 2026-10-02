using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using ProjectC.Domain.Members;
using ProjectC.Domain.Organizers;
using ProjectC.Domain.Venues;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Migrations;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests.Events;

/// <summary>
/// event-management-organizer-scoping tasks.md 7.9～7.12a（不含需要 WebApi 的 7.12，見
/// ProjectC.WebApi.Tests 的 EventOrganizerBackfillEndToEndTests）：實際執行 EF Core migration 驗證回填與 preflight。
/// 每個測試在同一個 Postgres 容器內建立自己的獨立資料庫，停在 AddOrganizers（本次遷移的前一支）以模擬
/// 部署前的既有資料。遷移前的 schema 沒有 Events.OrganizerId，而目前的 EF model 已把它當作必填欄位，
/// 所以既有 Event 只能以參數化原生 SQL 寫入／讀取，無法透過 EF Core 實體操作。
/// </summary>
[Collection(PostgresCollection.Name)]
public class EventOrganizerBackfillMigrationTests
{
    private const string MigrationBeforeScoping = "20260922142257_AddOrganizers";
    private const string BackfillMigration = "20260923090000_AddEventOrganizerId";
    private const string EnforceNotNullMigration = "20260923090100_EnforceEventOrganizerIdNotNull";
    private static readonly Guid LegacyOrganizerId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly PostgresFixture _fixture;

    public EventOrganizerBackfillMigrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<ApplicationDbContext> CreateDatabaseAtMigrationAsync(string targetMigration)
    {
        var databaseName = $"migration_{Guid.NewGuid():N}";
        await using (var serverContext = _fixture.CreateDbContext())
        {
            // 資料庫名稱是測試自己產生的 GUID，不含外部輸入；CREATE DATABASE 不支援參數化識別字。
            await serverContext.Database.ExecuteSqlRawAsync($"CREATE DATABASE \"{databaseName}\"");
        }

        var connectionString = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = databaseName }.ConnectionString;
        var dbContext = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connectionString).Options);
        await MigrateToAsync(dbContext, targetMigration);
        return dbContext;
    }

    private static Task MigrateToAsync(ApplicationDbContext dbContext, string targetMigration)
        => dbContext.GetService<IMigrator>().MigrateAsync(targetMigration);

    /// <summary>
    /// 以遷移前的 schema 寫入一筆既有 Member。目前的 EF model 已含之後才加入的實名欄位
    /// （real-name-verification），透過實體寫入會引用遷移前不存在的欄位。
    /// </summary>
    private static async Task<Guid> SeedMemberAsync(ApplicationDbContext dbContext, MemberRole role)
    {
        var memberId = Guid.NewGuid();
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Members" ("Id", "Email", "DisplayName", "PasswordHash", "Role", "IsActive")
            VALUES ({memberId}, {$"legacy-{memberId:N}@example.com"}, 'Legacy Member', 'hash', {(int)role}, true)
            """);
        return memberId;
    }

    /// <summary>
    /// 同 <see cref="OrganizerTestData.SeedApprovedOrganizerAsync"/>，但 Owner 以 <see cref="SeedMemberAsync"/> 寫入，
    /// 理由同該方法：此時資料庫停在較舊的 migration。
    /// </summary>
    private static async Task<Guid> SeedApprovedOrganizerAsync(ApplicationDbContext dbContext)
    {
        var ownerId = await SeedMemberAsync(dbContext, MemberRole.Member);
        var organizer = Organizer.Apply(Guid.NewGuid(), "Test Organizer", ownerId, DateTime.UtcNow);
        organizer.Approve(ownerId, DateTime.UtcNow);
        dbContext.Organizers.Add(organizer);
        await dbContext.SaveChangesAsync();
        return organizer.Id;
    }

    private static async Task<(Guid VenueId, Guid SeatMapId)> SeedVenueAsync(ApplicationDbContext dbContext)
    {
        var venue = new Venue(Guid.NewGuid(), "Legacy Venue");
        var seatMap = new SeatMap(Guid.NewGuid(), venue.Id);
        dbContext.Venues.Add(venue);
        dbContext.SeatMaps.Add(seatMap);
        await dbContext.SaveChangesAsync();
        return (venue.Id, seatMap.Id);
    }

    /// <summary>以遷移前的 schema（無 OrganizerId 欄位）寫入一筆既有 Event。</summary>
    private static async Task<Guid> SeedLegacyEventAsync(ApplicationDbContext dbContext, Guid venueId, Guid seatMapId, Guid? createdByMemberId)
    {
        var eventId = Guid.NewGuid();
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Events" ("Id", "Title", "StartAtUtc", "VenueId", "SeatMapId", "CreatedByMemberId", "IsQueueModeEnabled")
            VALUES ({eventId}, 'Legacy Event', {DateTime.UtcNow.AddDays(30)}, {venueId}, {seatMapId}, {createdByMemberId}, false)
            """);
        return eventId;
    }

    private static Task<List<Guid?>> GetEventOrganizerIdsAsync(ApplicationDbContext dbContext)
        => dbContext.Database.SqlQuery<Guid?>($"""SELECT "OrganizerId" AS "Value" FROM "Events" """).ToListAsync();

    private static async Task<bool> IsEventOrganizerIdNullableAsync(ApplicationDbContext dbContext)
        => await dbContext.Database.SqlQuery<string>($"""
            SELECT is_nullable AS "Value" FROM information_schema.columns
            WHERE table_name = 'Events' AND column_name = 'OrganizerId'
            """).SingleAsync() == "YES";

    private static Task<bool> EventOrganizerForeignKeyExistsAsync(ApplicationDbContext dbContext)
        => dbContext.Database.SqlQuery<int>($"""
            SELECT 1 AS "Value" FROM information_schema.table_constraints
            WHERE table_name = 'Events' AND constraint_name = 'FK_Events_Organizers_OrganizerId'
            """).AnyAsync();

    private static async Task<bool> MigrationAppliedAsync(ApplicationDbContext dbContext, string migrationId)
        => (await dbContext.Database.GetAppliedMigrationsAsync()).Contains(migrationId);

    // [EVT-MIGRATE-001] 所有既有 Admin（含從未建立過活動者）各自成為轉入用 Organizer 的 Owner；
    // 一般 Member 不得因回填而取得後台權限。
    [Fact]
    public async Task BackfillMigration_WithAdminsWithAndWithoutEvents_MakesEveryAdminAnOwnerOfLegacyOrganizer()
    {
        await using var dbContext = await CreateDatabaseAtMigrationAsync(MigrationBeforeScoping);
        var adminWithEvent = await SeedMemberAsync(dbContext, MemberRole.Admin);
        var adminWithoutEvent = await SeedMemberAsync(dbContext, MemberRole.Admin);
        var regularMember = await SeedMemberAsync(dbContext, MemberRole.Member);
        var (venueId, seatMapId) = await SeedVenueAsync(dbContext);
        await SeedLegacyEventAsync(dbContext, venueId, seatMapId, adminWithEvent);

        await MigrateToAsync(dbContext, BackfillMigration);

        var memberships = await dbContext.OrganizerMembers.AsNoTracking().Where(m => m.OrganizerId == LegacyOrganizerId).ToListAsync();
        memberships.Where(m => m.MemberId == adminWithEvent).Should().ContainSingle().Which.Role.Should().Be(OrganizerMemberRole.Owner);
        memberships.Where(m => m.MemberId == adminWithoutEvent).Should().ContainSingle().Which.Role.Should().Be(OrganizerMemberRole.Owner);
        memberships.Should().NotContain(m => m.MemberId == regularMember);
        var legacyOrganizer = await dbContext.Organizers.AsNoTracking().SingleAsync(o => o.Id == LegacyOrganizerId);
        legacyOrganizer.Status.Should().Be(OrganizerStatus.Approved);
    }

    // [EVT-MIGRATE-002] 所有既有 Event（含未記錄建立者的舊活動）皆回填為轉入用 Organizer。
    [Fact]
    public async Task BackfillMigration_WithExistingEvents_AssignsEveryEventToLegacyOrganizer()
    {
        await using var dbContext = await CreateDatabaseAtMigrationAsync(MigrationBeforeScoping);
        var adminId = await SeedMemberAsync(dbContext, MemberRole.Admin);
        var (venueId, seatMapId) = await SeedVenueAsync(dbContext);
        await SeedLegacyEventAsync(dbContext, venueId, seatMapId, adminId);
        await SeedLegacyEventAsync(dbContext, venueId, seatMapId, createdByMemberId: null);

        await MigrateToAsync(dbContext, BackfillMigration);

        var organizerIds = await GetEventOrganizerIdsAsync(dbContext);
        organizerIds.Should().HaveCount(2).And.OnlyContain(id => id == LegacyOrganizerId);
    }

    // 回填時沒有任何 Admin（轉入用 Organizer 建不起來）卻有待回填的 Event：MUST 以可辨識訊息中止，
    // 且整支 migration（含新增欄位）完全不套用，不得把 Event 指向不存在的 Organizer。
    [Fact]
    public async Task BackfillMigration_WithEventsButNoAdmin_AbortsWithIdentifiableErrorAndAppliesNothing()
    {
        await using var dbContext = await CreateDatabaseAtMigrationAsync(MigrationBeforeScoping);
        var (venueId, seatMapId) = await SeedVenueAsync(dbContext);
        await SeedLegacyEventAsync(dbContext, venueId, seatMapId, createdByMemberId: null);

        var act = () => MigrateToAsync(dbContext, BackfillMigration);

        (await act.Should().ThrowAsync<PostgresException>()).Which.MessageText
            .Should().Contain("AddEventOrganizerId backfill check failed");
        (await MigrationAppliedAsync(dbContext, BackfillMigration)).Should().BeFalse();
        (await dbContext.Database.SqlQuery<int>($"""
            SELECT 1 AS "Value" FROM information_schema.columns WHERE table_name = 'Events' AND column_name = 'OrganizerId'
            """).AnyAsync()).Should().BeFalse("中止時整支 migration 在同一交易內回滾，欄位不得殘留");
    }

    // [EVT-MIGRATE-003] 部署窗口期殘留 OrganizerId IS NULL 的列時，NOT NULL 遷移 MUST 由內建 preflight 明確中止，
    // 且 NOT NULL 約束與外鍵皆未套用。
    [Fact]
    public async Task EnforceNotNullMigration_WithResidualNullOrganizerId_AbortsWithIdentifiableErrorAndAppliesNoConstraint()
    {
        await using var dbContext = await CreateDatabaseAtMigrationAsync(MigrationBeforeScoping);
        await SeedMemberAsync(dbContext, MemberRole.Admin);
        var (venueId, seatMapId) = await SeedVenueAsync(dbContext);
        await MigrateToAsync(dbContext, BackfillMigration);
        // 模擬回填完成後、新版後端上線前，舊版後端建立的不帶 OrganizerId 的活動。
        await SeedLegacyEventAsync(dbContext, venueId, seatMapId, createdByMemberId: null);

        var act = () => MigrateToAsync(dbContext, EnforceNotNullMigration);

        (await act.Should().ThrowAsync<PostgresException>()).Which.MessageText
            .Should().Contain("EnforceEventOrganizerIdNotNull preflight check failed")
            .And.Contain("OrganizerId IS NULL");
        (await MigrationAppliedAsync(dbContext, EnforceNotNullMigration)).Should().BeFalse();
        (await IsEventOrganizerIdNullableAsync(dbContext)).Should().BeTrue();
        (await EventOrganizerForeignKeyExistsAsync(dbContext)).Should().BeFalse();
    }

    // Event 指向不存在的 Organizer（孤兒列）時，NOT NULL 遷移同樣 MUST 由 preflight 明確中止，
    // 不依賴 AddForeignKey 碰巧以泛用 FK 錯誤失敗。
    [Fact]
    public async Task EnforceNotNullMigration_WithOrphanOrganizerId_AbortsWithIdentifiableErrorAndAppliesNoConstraint()
    {
        await using var dbContext = await CreateDatabaseAtMigrationAsync(MigrationBeforeScoping);
        await SeedMemberAsync(dbContext, MemberRole.Admin);
        var (venueId, seatMapId) = await SeedVenueAsync(dbContext);
        await SeedLegacyEventAsync(dbContext, venueId, seatMapId, createdByMemberId: null);
        await MigrateToAsync(dbContext, BackfillMigration);
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "Events" SET "OrganizerId" = {Guid.NewGuid()}""");

        var act = () => MigrateToAsync(dbContext, EnforceNotNullMigration);

        (await act.Should().ThrowAsync<PostgresException>()).Which.MessageText
            .Should().Contain("EnforceEventOrganizerIdNotNull preflight check failed")
            .And.Contain("does not exist in Organizers");
        (await IsEventOrganizerIdNullableAsync(dbContext)).Should().BeTrue();
        (await EventOrganizerForeignKeyExistsAsync(dbContext)).Should().BeFalse();
    }

    // 正向對照：回填完整時 NOT NULL 遷移成功套用約束與外鍵（確保上面兩個中止測試不是因為遷移本身壞掉才失敗）。
    [Fact]
    public async Task EnforceNotNullMigration_AfterCompleteBackfill_AppliesNotNullAndForeignKey()
    {
        await using var dbContext = await CreateDatabaseAtMigrationAsync(MigrationBeforeScoping);
        await SeedMemberAsync(dbContext, MemberRole.Admin);
        var (venueId, seatMapId) = await SeedVenueAsync(dbContext);
        await SeedLegacyEventAsync(dbContext, venueId, seatMapId, createdByMemberId: null);

        await MigrateToAsync(dbContext, EnforceNotNullMigration);

        (await IsEventOrganizerIdNullableAsync(dbContext)).Should().BeFalse();
        (await EventOrganizerForeignKeyExistsAsync(dbContext)).Should().BeTrue();
    }

    // [EVT-MIGRATE-005] 回填腳本中途失敗後人工重跑整支腳本：不得產生重複 Organizer／OrganizerMember，
    // 也不得改寫已回填（或回填後已被改歸屬）的 Event.OrganizerId。
    [Fact]
    public async Task BackfillScripts_WhenRerunOnAlreadyBackfilledDatabase_CreateNoDuplicatesAndRewriteNoEvent()
    {
        await using var dbContext = await CreateDatabaseAtMigrationAsync(MigrationBeforeScoping);
        var adminIds = new[] { await SeedMemberAsync(dbContext, MemberRole.Admin), await SeedMemberAsync(dbContext, MemberRole.Admin) };
        var (venueId, seatMapId) = await SeedVenueAsync(dbContext);
        var reassignedEventId = await SeedLegacyEventAsync(dbContext, venueId, seatMapId, adminIds[0]);
        await SeedLegacyEventAsync(dbContext, venueId, seatMapId, adminIds[1]);
        await MigrateToAsync(dbContext, BackfillMigration);
        // 回填後把其中一筆改歸屬到另一個真實 Organizer：若重跑的 UPDATE 沒有 WHERE OrganizerId IS NULL 限制，
        // 這筆會被改回轉入用 Organizer，藉此讓「未被改寫」的斷言真的能失敗。
        var otherOrganizerId = await SeedApprovedOrganizerAsync(dbContext);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "Events" SET "OrganizerId" = {otherOrganizerId} WHERE "Id" = {reassignedEventId}""");
        var organizerIdsBeforeRerun = await GetEventOrganizerIdsAsync(dbContext);

        foreach (var sqlOperation in new AddEventOrganizerId().UpOperations.OfType<SqlOperation>())
        {
            await dbContext.Database.ExecuteSqlRawAsync(sqlOperation.Sql);
        }

        (await dbContext.Organizers.AsNoTracking().CountAsync(o => o.Id == LegacyOrganizerId)).Should().Be(1);
        foreach (var adminId in adminIds)
        {
            (await dbContext.OrganizerMembers.AsNoTracking()
                .CountAsync(m => m.OrganizerId == LegacyOrganizerId && m.MemberId == adminId)).Should().Be(1);
        }
        (await GetEventOrganizerIdsAsync(dbContext)).Should().BeEquivalentTo(organizerIdsBeforeRerun);
        organizerIdsBeforeRerun.Should().Contain(otherOrganizerId);
    }
}
