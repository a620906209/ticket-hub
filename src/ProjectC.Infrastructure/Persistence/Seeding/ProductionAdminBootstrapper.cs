using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Members;

namespace ProjectC.Infrastructure.Persistence.Seeding;

/// <summary>
/// 非 Development 環境：資料庫完全沒有 Admin 時，依 <see cref="AdminBootstrapOptions"/> 建立首位 Admin
/// （production-admin-bootstrap）。已有任何 Admin（不論是否啟用）一律略過，不比對 Email、不覆寫；不建立 Organizer。
/// 日誌與例外一律不含 Email、密碼與雜湊。
/// </summary>
public sealed class ProductionAdminBootstrapper
{
    // 對應 MemberConfiguration 的 Email 唯一索引（InitialCreate migration 的索引名稱）。
    private const string EmailUniqueIndexName = "IX_Members_Email";

    private readonly ApplicationDbContext _dbContext;
    private readonly IPasswordHasher _passwordHasher;
    private readonly AdminBootstrapOptions _options;
    private readonly ILogger<ProductionAdminBootstrapper> _logger;

    public ProductionAdminBootstrapper(
        ApplicationDbContext dbContext,
        IPasswordHasher passwordHasher,
        IOptions<AdminBootstrapOptions> options,
        ILogger<ProductionAdminBootstrapper> logger)
    {
        _dbContext = dbContext;
        _passwordHasher = passwordHasher;
        _options = options.Value;
        _logger = logger;
    }

    public async Task BootstrapAsync(CancellationToken cancellationToken)
    {
        var isEmailBlank = string.IsNullOrWhiteSpace(_options.Email);
        var isPasswordBlank = string.IsNullOrWhiteSpace(_options.Password);
        if (isEmailBlank && isPasswordBlank)
        {
            _logger.LogInformation("Admin bootstrap skipped: {Section}:Email / Password not configured.", AdminBootstrapOptions.SectionName);
            return;
        }

        // 正常啟動由 AdminBootstrapOptionsValidator 先擋下；此處是直接呼叫時的最後防線，訊息只含設定項名稱。
        if (isEmailBlank || isPasswordBlank)
        {
            throw new InvalidOperationException(
                $"{AdminBootstrapOptions.SectionName}:Email 與 {AdminBootstrapOptions.SectionName}:Password 須同時設定。");
        }

        var pendingMigrations = (await _dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pendingMigrations.Count > 0)
        {
            _logger.LogWarning(
                "Admin bootstrap skipped: {PendingMigrationCount} pending migration(s). Apply migrations and restart.",
                pendingMigrations.Count);
            return;
        }

        if (await HasAnyAdminAsync(cancellationToken))
        {
            _logger.LogInformation("Admin bootstrap skipped: an Admin already exists.");
            return;
        }

        await CreateAdminAsync(cancellationToken);
    }

    private Task<bool> HasAnyAdminAsync(CancellationToken cancellationToken)
        => _dbContext.Members.AnyAsync(member => member.Role == MemberRole.Admin, cancellationToken);

    private async Task CreateAdminAsync(CancellationToken cancellationToken)
    {
        var admin = Member.Register(_options.Email, _options.DisplayName, _passwordHasher.HashPassword(_options.Password));
        // Domain 目前沒有公開的「指派角色」流程，比照 DevelopmentDataSeeder 經 EF Core 直接設定 Role（已知技術債）。
        _dbContext.Entry(admin).Property(member => member.Role).CurrentValue = MemberRole.Admin;
        _dbContext.Members.Add(admin);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsEmailUniqueViolation(exception))
        {
            _dbContext.ChangeTracker.Clear();
            await HandleEmailConflictAsync(cancellationToken);
            return;
        }

        _logger.LogInformation("Admin bootstrap: created admin account {MemberId}.", admin.Id);
    }

    // 多副本並發啟動時，另一個實例可能已搶先建立；重新確認已有 Admin 即視為成功，否則表示相同 Email 屬於非 Admin 會員。
    private async Task HandleEmailConflictAsync(CancellationToken cancellationToken)
    {
        if (await HasAnyAdminAsync(cancellationToken))
        {
            _logger.LogInformation("Admin bootstrap skipped: an Admin was created concurrently.");
            return;
        }

        // 刻意不帶內部例外：Postgres 的衝突訊息會含 Email 值（Key (Email)=(...)），不得進入啟動失敗訊息。
        throw new InvalidOperationException(
            $"{AdminBootstrapOptions.SectionName}:Email 設定的帳號已被非 Admin 會員使用，無法建立首位 Admin；該會員未被修改。");
    }

    private static bool IsEmailUniqueViolation(DbUpdateException exception)
        => exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: EmailUniqueIndexName,
        };
}
