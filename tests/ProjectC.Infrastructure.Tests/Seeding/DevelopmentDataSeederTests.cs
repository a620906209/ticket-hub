using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ProjectC.Domain.Members;
using ProjectC.Domain.Organizers;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Seeding;
using ProjectC.Infrastructure.Security;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests.Seeding;

/// <summary>
/// event-management-organizer-scoping tasks.md 6.2：新環境啟動後，設定的 Admin 帳號必須能直接登入、切換到一個
/// Approved Organizer 操作後台；seeder 每次啟動都會執行，所以重跑不得產生重複資料，也不得改寫既有帳號。
/// </summary>
[Collection(PostgresCollection.Name)]
public class DevelopmentDataSeederTests
{
    private const string Password = "Seed-Passw0rd";
    private readonly PostgresFixture _fixture;
    private readonly BCryptPasswordHasher _passwordHasher = new();

    public DevelopmentDataSeederTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static string NewEmail() => $"seed-admin-{Guid.NewGuid():N}@example.com";

    private DevelopmentDataSeeder CreateSeeder(ApplicationDbContext dbContext, string adminEmail, string adminPassword = Password)
        => new(
            dbContext,
            _passwordHasher,
            new FakeDateTimeProvider(),
            Options.Create(new DevelopmentSeedOptions { AdminEmail = adminEmail, AdminPassword = adminPassword, OrganizerName = "Seed Organizer" }),
            NullLogger<DevelopmentDataSeeder>.Instance);

    private async Task SeedWithFreshContextAsync(string adminEmail, string adminPassword = Password)
    {
        await using var dbContext = _fixture.CreateDbContext();
        await CreateSeeder(dbContext, adminEmail, adminPassword).SeedAsync(CancellationToken.None);
    }

    private async Task<(Member Admin, List<Organizer> OwnedApprovedOrganizers)> LoadSeededStateAsync(string adminEmail)
    {
        await using var dbContext = _fixture.CreateDbContext();
        var admin = await dbContext.Members.AsNoTracking().SingleAsync(m => m.Email == adminEmail);
        var organizers = await dbContext.OrganizerMembers.AsNoTracking()
            .Where(om => om.MemberId == admin.Id && om.Role == OrganizerMemberRole.Owner)
            .Join(dbContext.Organizers.AsNoTracking(), om => om.OrganizerId, o => o.Id, (_, o) => o)
            .Where(o => o.Status == OrganizerStatus.Approved)
            .ToListAsync();
        return (admin, organizers);
    }

    [Fact]
    public async Task SeedAsync_WhenConfigured_CreatesAdminWhoOwnsOneApprovedOrganizer()
    {
        var email = NewEmail();

        await SeedWithFreshContextAsync(email);

        var (admin, organizers) = await LoadSeededStateAsync(email);
        admin.Role.Should().Be(MemberRole.Admin);
        admin.IsActive.Should().BeTrue();
        _passwordHasher.VerifyPassword(Password, admin.PasswordHash).Should().BeTrue("設定的密碼必須能直接登入");
        organizers.Should().ContainSingle().Which.Name.Should().Be("Seed Organizer");
    }

    [Fact]
    public async Task SeedAsync_WhenRunOnEveryStartup_CreatesNoDuplicateAdminOrOrganizer()
    {
        var email = NewEmail();

        await SeedWithFreshContextAsync(email);
        await SeedWithFreshContextAsync(email);

        await using var dbContext = _fixture.CreateDbContext();
        (await dbContext.Members.CountAsync(m => m.Email == email)).Should().Be(1);
        var (_, organizers) = await LoadSeededStateAsync(email);
        organizers.Should().ContainSingle();
    }

    [Fact]
    public async Task SeedAsync_WhenAdminCredentialsNotConfigured_WritesNothing()
    {
        await using var dbContext = _fixture.CreateDbContext();
        var memberCountBefore = await dbContext.Members.CountAsync();
        var organizerCountBefore = await dbContext.Organizers.CountAsync();

        await CreateSeeder(dbContext, adminEmail: string.Empty, adminPassword: string.Empty).SeedAsync(CancellationToken.None);

        (await dbContext.Members.CountAsync()).Should().Be(memberCountBefore);
        (await dbContext.Organizers.CountAsync()).Should().Be(organizerCountBefore);
    }

    // 設定的 Email 已被開發者自己註冊成一般會員：不得偷偷升為 Admin 或覆寫密碼。
    [Fact]
    public async Task SeedAsync_WhenConfiguredEmailBelongsToExistingNonAdmin_LeavesRoleAndPasswordUnchanged()
    {
        var email = NewEmail();
        var originalHash = _passwordHasher.HashPassword("Original-Passw0rd");
        await using (var dbContext = _fixture.CreateDbContext())
        {
            dbContext.Members.Add(Member.Register(email, "Existing Member", originalHash));
            await dbContext.SaveChangesAsync();
        }

        await SeedWithFreshContextAsync(email);

        var (member, _) = await LoadSeededStateAsync(email);
        member.Role.Should().Be(MemberRole.Member);
        member.PasswordHash.Should().Be(originalHash);
    }

    // migration 尚未套用時，寫入只會以缺表／缺欄位例外讓 API 起不來；MUST 略過且不寫入任何資料。
    [Fact]
    public async Task SeedAsync_WithPendingMigrations_SkipsWithoutWriting()
    {
        var databaseName = $"seed_{Guid.NewGuid():N}";
        await using (var serverContext = _fixture.CreateDbContext())
        {
            // 資料庫名稱是測試自己產生的 GUID，不含外部輸入；CREATE DATABASE 不支援參數化識別字。
            await serverContext.Database.ExecuteSqlRawAsync($"CREATE DATABASE \"{databaseName}\"");
        }
        var connectionString = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = databaseName }.ConnectionString;
        await using var dbContext = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connectionString).Options);
        await dbContext.GetService<IMigrator>().MigrateAsync("20260922142257_AddOrganizers");

        await CreateSeeder(dbContext, NewEmail()).SeedAsync(CancellationToken.None);

        (await dbContext.Members.CountAsync()).Should().Be(0);
        (await dbContext.Organizers.CountAsync()).Should().Be(0);
    }
}
