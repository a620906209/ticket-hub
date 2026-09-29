using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Members;
using ProjectC.Domain.Organizers;

namespace ProjectC.Infrastructure.Persistence.Seeding;

/// <summary>
/// 本機開發用：確保存在一個 Admin 帳號，且該帳號是一個 Approved Organizer 的 Owner，讓新環境啟動後
/// 登入、切換一次主辦方即可操作 Organizer-scoped 後台（event-management-organizer-scoping tasks.md 6.2）。
/// 正式環境不執行——既有 Admin 由 AddEventOrganizerId migration 回填涵蓋。每次啟動都可安全重跑（冪等）。
/// </summary>
public sealed class DevelopmentDataSeeder
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly DevelopmentSeedOptions _options;
    private readonly ILogger<DevelopmentDataSeeder> _logger;

    public DevelopmentDataSeeder(
        ApplicationDbContext dbContext,
        IPasswordHasher passwordHasher,
        IDateTimeProvider dateTimeProvider,
        IOptions<DevelopmentSeedOptions> options,
        ILogger<DevelopmentDataSeeder> logger)
    {
        _dbContext = dbContext;
        _passwordHasher = passwordHasher;
        _dateTimeProvider = dateTimeProvider;
        _options = options.Value;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.AdminEmail) || string.IsNullOrWhiteSpace(_options.AdminPassword))
        {
            _logger.LogInformation("Development seed skipped: {Section}:AdminEmail / AdminPassword not configured.", DevelopmentSeedOptions.SectionName);
            return;
        }

        // 本專案的 migration 由開發者手動 `dotnet ef database update`，啟動當下 schema 可能還沒到位；
        // 此時寫入只會以缺表／缺欄位的例外讓整個 API 起不來，改為明確警告並略過，套用後重啟即會補上。
        var pendingMigrations = (await _dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pendingMigrations.Count > 0)
        {
            _logger.LogWarning(
                "Development seed skipped: {PendingMigrationCount} pending migration(s). Run `docker compose exec api dotnet ef database update` and restart.",
                pendingMigrations.Count);
            return;
        }

        var admin = await EnsureAdminAsync(cancellationToken);
        await EnsureApprovedOrganizerOwnedByAsync(admin.Id, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<Member> EnsureAdminAsync(CancellationToken cancellationToken)
    {
        var existing = await _dbContext.Members.FirstOrDefaultAsync(m => m.Email == _options.AdminEmail, cancellationToken);
        if (existing is not null)
        {
            // 不改寫既有帳號的密碼或角色：帳號可能是開發者自己註冊、刻意維持的狀態。
            if (existing.Role != MemberRole.Admin)
            {
                _logger.LogWarning("Development seed: configured admin account {MemberId} exists but is not an Admin; left unchanged.", existing.Id);
            }

            return existing;
        }

        var admin = Member.Register(_options.AdminEmail, _options.AdminDisplayName, _passwordHasher.HashPassword(_options.AdminPassword));
        // Domain 目前沒有公開的「指派角色」流程（非任何既有 spec 範圍），比照 migration 回填直接寫入資料的定位，
        // 由 Infrastructure 層的 seeder 經 EF Core 直接設定 Role。
        _dbContext.Entry(admin).Property(m => m.Role).CurrentValue = MemberRole.Admin;
        _dbContext.Members.Add(admin);
        _logger.LogInformation("Development seed: created admin account {MemberId}.", admin.Id);
        return admin;
    }

    private async Task EnsureApprovedOrganizerOwnedByAsync(Guid adminId, CancellationToken cancellationToken)
    {
        var alreadyOwnsApprovedOrganizer = await _dbContext.OrganizerMembers
            .Where(om => om.MemberId == adminId && om.Role == OrganizerMemberRole.Owner)
            .Join(_dbContext.Organizers, om => om.OrganizerId, o => o.Id, (_, o) => o)
            .AnyAsync(o => o.Status == OrganizerStatus.Approved && o.Name == _options.OrganizerName, cancellationToken);
        if (alreadyOwnsApprovedOrganizer)
        {
            return;
        }

        var now = _dateTimeProvider.UtcNow;
        var organizer = Organizer.Apply(Guid.NewGuid(), _options.OrganizerName, adminId, now);
        organizer.Approve(adminId, now);
        _dbContext.Organizers.Add(organizer);
        _dbContext.OrganizerMembers.Add(new OrganizerMember(Guid.NewGuid(), organizer.Id, adminId, OrganizerMemberRole.Owner));
        _logger.LogInformation("Development seed: created approved organizer {OrganizerId} owned by {MemberId}.", organizer.Id, adminId);
    }
}
