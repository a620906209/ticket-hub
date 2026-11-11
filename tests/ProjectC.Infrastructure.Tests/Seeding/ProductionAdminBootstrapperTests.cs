using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using ProjectC.Domain.Members;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Seeding;
using ProjectC.Infrastructure.Security;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests.Seeding;

/// <summary>
/// production-admin-bootstrap ADMIN-BOOT-001／002／003／005／008／010：只在資料庫完全沒有 Admin 時才建立首位 Admin，
/// 且任何路徑的日誌與例外都不得洩漏密碼、雜湊或 Email。每個測試使用獨立的全新資料庫，
/// 因為「資料庫無任何 Admin」是前置條件，不能與共用 fixture 資料庫內其他測試建立的 Admin 互相污染。
/// </summary>
[Collection(PostgresCollection.Name)]
public class ProductionAdminBootstrapperTests
{
    private const string Password = "Boot-Passw0rd";
    private const string BCryptHashPrefix = "$2";
    private readonly PostgresFixture _fixture;
    private readonly BCryptPasswordHasher _passwordHasher = new();

    public ProductionAdminBootstrapperTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static string NewEmail() => $"boot-admin-{Guid.NewGuid():N}@example.com";

    private async Task<string> CreateIsolatedDatabaseAsync(string? migrateTo = null)
    {
        var databaseName = $"bootstrap_{Guid.NewGuid():N}";
        await using (var serverContext = _fixture.CreateDbContext())
        {
            // 資料庫名稱是測試自己產生的 GUID，不含外部輸入；CREATE DATABASE 不支援參數化識別字。
            #pragma warning disable EF1002 // 理由同上方註解
            await serverContext.Database.ExecuteSqlRawAsync($"CREATE DATABASE \"{databaseName}\"");
            #pragma warning restore EF1002
        }

        var connectionString = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Database = databaseName }.ConnectionString;
        await using var migrationContext = CreateDbContext(connectionString);
        await migrationContext.GetService<IMigrator>().MigrateAsync(migrateTo);
        return connectionString;
    }

    private static ApplicationDbContext CreateDbContext(string connectionString, params IInterceptor[] interceptors)
        => new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connectionString).AddInterceptors(interceptors).Options);

    private ProductionAdminBootstrapper CreateBootstrapper(
        ApplicationDbContext dbContext, ListLogger<ProductionAdminBootstrapper> logger, string? email, string? password = Password)
        => new(
            dbContext,
            _passwordHasher,
            Options.Create(new AdminBootstrapOptions { Email = email ?? string.Empty, Password = password ?? string.Empty }),
            logger);

    private async Task AddMemberAsync(string connectionString, string email, bool isAdmin = false, bool isActive = true)
    {
        await using var dbContext = CreateDbContext(connectionString);
        var member = Member.Register(email, "Existing", _passwordHasher.HashPassword("Original-Passw0rd"));
        if (isAdmin)
        {
            dbContext.Entry(member).Property(m => m.Role).CurrentValue = MemberRole.Admin;
        }

        if (!isActive)
        {
            member.Deactivate();
        }

        dbContext.Members.Add(member);
        await dbContext.SaveChangesAsync();
    }

    private static async Task<List<Member>> LoadMembersAsync(string connectionString)
    {
        await using var dbContext = CreateDbContext(connectionString);
        return await dbContext.Members.AsNoTracking().OrderBy(m => m.Email).ToListAsync();
    }

    private static string RenderAllLogText(ListLogger<ProductionAdminBootstrapper> logger)
        => string.Join(
            Environment.NewLine,
            logger.Entries.Select(entry => entry.Message
                + " " + string.Join(" ", entry.State.Values.Select(value => value?.ToString()))
                + " " + entry.Exception));

    [Fact]
    public async Task BootstrapAsync_WhenNoAdminExists_CreatesActiveAdminWithHashedPasswordAndNoOrganizer()
    {
        var connectionString = await CreateIsolatedDatabaseAsync();
        var email = NewEmail();
        var logger = new ListLogger<ProductionAdminBootstrapper>();

        await using (var dbContext = CreateDbContext(connectionString))
        {
            await CreateBootstrapper(dbContext, logger, email).BootstrapAsync(CancellationToken.None);
        }

        var members = await LoadMembersAsync(connectionString);
        var admin = members.Should().ContainSingle().Which;
        admin.Email.Should().Be(email);
        admin.Role.Should().Be(MemberRole.Admin);
        admin.IsActive.Should().BeTrue();
        admin.PasswordHash.Should().NotBe(Password).And.StartWith(BCryptHashPrefix);
        _passwordHasher.VerifyPassword(Password, admin.PasswordHash).Should().BeTrue("設定的密碼必須能經登入流程驗證");
        await using var verifyContext = CreateDbContext(connectionString);
        (await verifyContext.Organizers.CountAsync()).Should().Be(0);
        (await verifyContext.OrganizerMembers.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task BootstrapAsync_WhenRunTwice_CreatesOnlyOneAdmin()
    {
        var connectionString = await CreateIsolatedDatabaseAsync();
        var email = NewEmail();

        for (var run = 0; run < 2; run++)
        {
            await using var dbContext = CreateDbContext(connectionString);
            await CreateBootstrapper(dbContext, new ListLogger<ProductionAdminBootstrapper>(), email).BootstrapAsync(CancellationToken.None);
        }

        (await LoadMembersAsync(connectionString)).Should().ContainSingle();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BootstrapAsync_WhenAnyAdminExists_ChangesNothingAndLogsInformation(bool isExistingAdminActive)
    {
        var connectionString = await CreateIsolatedDatabaseAsync();
        await AddMemberAsync(connectionString, NewEmail(), isAdmin: true, isActive: isExistingAdminActive);
        var before = await LoadMembersAsync(connectionString);
        var logger = new ListLogger<ProductionAdminBootstrapper>();

        await using (var dbContext = CreateDbContext(connectionString))
        {
            await CreateBootstrapper(dbContext, logger, NewEmail()).BootstrapAsync(CancellationToken.None);
        }

        var after = await LoadMembersAsync(connectionString);
        after.Should().BeEquivalentTo(before);
        logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Information);
    }

    [Fact]
    public async Task BootstrapAsync_WhenConfiguredEmailBelongsToNonAdmin_ThrowsWithoutEmailInMessageAndLeavesMemberUnchanged()
    {
        var connectionString = await CreateIsolatedDatabaseAsync();
        var email = NewEmail();
        var otherEmail = NewEmail();
        await AddMemberAsync(connectionString, email);
        await AddMemberAsync(connectionString, otherEmail);
        var before = await LoadMembersAsync(connectionString);
        var logger = new ListLogger<ProductionAdminBootstrapper>();

        Exception thrown;
        await using (var dbContext = CreateDbContext(connectionString))
        {
            var act = () => CreateBootstrapper(dbContext, logger, email).BootstrapAsync(CancellationToken.None);
            thrown = (await act.Should().ThrowAsync<Exception>()).Which;
        }

        thrown.ToString().Should().NotContain(email).And.NotContain(otherEmail).And.NotContain(Password);
        RenderAllLogText(logger).Should().NotContain(email).And.NotContain(otherEmail).And.NotContain(Password);
        (await LoadMembersAsync(connectionString)).Should().BeEquivalentTo(before);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("  ", "  ")]
    public async Task BootstrapAsync_WhenNotConfigured_WritesNothingAndLogsInformation(string? email, string? password)
    {
        var connectionString = await CreateIsolatedDatabaseAsync();
        var logger = new ListLogger<ProductionAdminBootstrapper>();

        await using (var dbContext = CreateDbContext(connectionString))
        {
            await CreateBootstrapper(dbContext, logger, email, password).BootstrapAsync(CancellationToken.None);
        }

        (await LoadMembersAsync(connectionString)).Should().BeEmpty();
        logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Information);
    }

    [Theory]
    [InlineData("only-email@example.com", null)]
    [InlineData(null, Password)]
    public async Task BootstrapAsync_WhenOnlyOneSettingConfigured_ThrowsWithoutWritingOrLeakingPassword(string? email, string? password)
    {
        var connectionString = await CreateIsolatedDatabaseAsync();
        var logger = new ListLogger<ProductionAdminBootstrapper>();

        await using var dbContext = CreateDbContext(connectionString);
        var act = () => CreateBootstrapper(dbContext, logger, email, password).BootstrapAsync(CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        thrown.Message.Should().Contain("AdminBootstrap").And.NotContain(Password);
        (await LoadMembersAsync(connectionString)).Should().BeEmpty();
    }

    // migration 尚未套用時，寫入只會以缺表／缺欄位例外讓 API 起不來；MUST 略過、以 Warning 記錄未套用數量且不寫入。
    [Fact]
    public async Task BootstrapAsync_WithPendingMigrations_SkipsWithWarningContainingPendingCount()
    {
        var connectionString = await CreateIsolatedDatabaseAsync(migrateTo: "20260922142257_AddOrganizers");
        var logger = new ListLogger<ProductionAdminBootstrapper>();

        int expectedPendingCount;
        await using (var dbContext = CreateDbContext(connectionString))
        {
            expectedPendingCount = (await dbContext.Database.GetPendingMigrationsAsync()).Count();
            await CreateBootstrapper(dbContext, logger, NewEmail()).BootstrapAsync(CancellationToken.None);
        }

        expectedPendingCount.Should().BeGreaterThan(0);
        var warning = logger.Entries.Should().ContainSingle().Which;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.Message.Should().Contain(expectedPendingCount.ToString());
        await using var verifyContext = CreateDbContext(connectionString);
        (await verifyContext.Members.CountAsync()).Should().Be(0);
    }

    // 例外過濾不得過寬：只有「Email 唯一索引的 23505」才視為已存在，其餘一律重拋。
    [Theory]
    [InlineData("23503", "FK_Something")]
    [InlineData("23505", "IX_Other_Unique")]
    public async Task BootstrapAsync_WhenSaveFailsForUnrelatedConstraint_RethrowsOriginalDbUpdateException(string sqlState, string constraintName)
    {
        var connectionString = await CreateIsolatedDatabaseAsync();
        var failure = new ThrowingSaveInterceptor(sqlState, constraintName);

        await using var dbContext = CreateDbContext(connectionString, failure);
        var act = () => CreateBootstrapper(dbContext, new ListLogger<ProductionAdminBootstrapper>(), NewEmail()).BootstrapAsync(CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<DbUpdateException>()).Which;
        thrown.Should().BeSameAs(failure.ThrownException);
        (await LoadMembersAsync(connectionString)).Should().BeEmpty();
    }

    [Fact]
    public async Task BootstrapAsync_WhenCancellationRequested_ThrowsOperationCanceledException()
    {
        var connectionString = await CreateIsolatedDatabaseAsync();
        using var cancellationSource = new CancellationTokenSource();
        await cancellationSource.CancelAsync();

        await using var dbContext = CreateDbContext(connectionString);
        var act = () => CreateBootstrapper(dbContext, new ListLogger<ProductionAdminBootstrapper>(), NewEmail())
            .BootstrapAsync(cancellationSource.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await LoadMembersAsync(connectionString)).Should().BeEmpty();
    }

    // 資料庫不可達時 fail-closed：例外外洩使行程退出由平台重啟，而不是靜默略過。
    [Fact]
    public async Task BootstrapAsync_WhenDatabaseUnreachable_Throws()
    {
        var unreachable = new NpgsqlConnectionStringBuilder(_fixture.ConnectionString) { Host = "127.0.0.1", Port = 1, Timeout = 2 }.ConnectionString;

        await using var dbContext = CreateDbContext(unreachable);
        var act = () => CreateBootstrapper(dbContext, new ListLogger<ProductionAdminBootstrapper>(), NewEmail()).BootstrapAsync(CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task BootstrapAsync_OnCreateAndSkipPaths_NeverLogsPasswordHashOrEmail()
    {
        var connectionString = await CreateIsolatedDatabaseAsync();
        var email = NewEmail();
        var createLogger = new ListLogger<ProductionAdminBootstrapper>();
        await using (var dbContext = CreateDbContext(connectionString))
        {
            await CreateBootstrapper(dbContext, createLogger, email).BootstrapAsync(CancellationToken.None);
        }

        var skipLogger = new ListLogger<ProductionAdminBootstrapper>();
        await using (var dbContext = CreateDbContext(connectionString))
        {
            await CreateBootstrapper(dbContext, skipLogger, email).BootstrapAsync(CancellationToken.None);
        }

        var storedHash = (await LoadMembersAsync(connectionString)).Single().PasswordHash;
        foreach (var logger in new[] { createLogger, skipLogger })
        {
            logger.Entries.Should().NotBeEmpty();
            RenderAllLogText(logger).Should().NotContain(Password).And.NotContain(storedHash).And.NotContain(email);
        }
    }

    // ADMIN-BOOT-009 確定性版本：bootstrapper 通過「無 Admin」檢查、尚未寫入的空檔，另一個實例搶先建立相同 Email 的 Admin，
    // 讓 bootstrapper 的寫入實際收到 Email 唯一索引的 23505；不得只斷言最終資料，必須證明確實走過恢復分支。
    [Fact]
    public async Task BootstrapAsync_WhenAnotherInstanceCreatesSameAdminBeforeSave_TreatsEmailConflictAsSuccess()
    {
        var connectionString = await CreateIsolatedDatabaseAsync();
        var email = NewEmail();
        var competitorPassword = "Competitor-Passw0rd";
        var competitor = new CompetingInsertInterceptor(async () =>
        {
            await using var otherContext = CreateDbContext(connectionString);
            var otherAdmin = Member.Register(email, "Competitor", _passwordHasher.HashPassword(competitorPassword));
            otherContext.Entry(otherAdmin).Property(m => m.Role).CurrentValue = MemberRole.Admin;
            otherContext.Members.Add(otherAdmin);
            await otherContext.SaveChangesAsync();
        });
        var logger = new ListLogger<ProductionAdminBootstrapper>();

        await using (var dbContext = CreateDbContext(connectionString, competitor))
        {
            var act = () => CreateBootstrapper(dbContext, logger, email).BootstrapAsync(CancellationToken.None);
            await act.Should().NotThrowAsync();
        }

        competitor.SaveFailures.Should().ContainSingle("bootstrapper 的寫入必須實際收到唯一性衝突，才算走過恢復分支")
            .Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.Should().Match<PostgresException>(e => e.SqlState == "23505" && e.ConstraintName == "IX_Members_Email");
        var members = await LoadMembersAsync(connectionString);
        var admin = members.Should().ContainSingle().Which;
        admin.Email.Should().Be(email);
        admin.Role.Should().Be(MemberRole.Admin);
        _passwordHasher.VerifyPassword(competitorPassword, admin.PasswordHash).Should().BeTrue("先成功者的帳號不得被覆寫");
        logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Information && entry.Message.Contains("concurrently"));
    }

    // ADMIN-BOOT-009 壓力版本：兩個獨立 DbContext 同時執行，每一輪都必須恰好一位 Admin 且兩者皆完成。
    [Fact]
    public async Task BootstrapAsync_WhenTwoInstancesRunConcurrently_CreatesExactlyOneAdminAndBothComplete()
    {
        var connectionString = await CreateIsolatedDatabaseAsync();
        var email = NewEmail();

        for (var round = 0; round < 10; round++)
        {
            await using (var cleanupContext = CreateDbContext(connectionString))
            {
                await cleanupContext.Members.ExecuteDeleteAsync();
            }

            var startSignal = new TaskCompletionSource();
            var instances = Enumerable.Range(0, 2).Select(async _ =>
            {
                await using var dbContext = CreateDbContext(connectionString);
                var bootstrapper = CreateBootstrapper(dbContext, new ListLogger<ProductionAdminBootstrapper>(), email);
                await startSignal.Task;
                await bootstrapper.BootstrapAsync(CancellationToken.None);
            }).ToList();
            startSignal.SetResult();

            var act = () => Task.WhenAll(instances);
            await act.Should().NotThrowAsync($"第 {round} 輪兩個實例皆須正常完成");
            var members = await LoadMembersAsync(connectionString);
            members.Should().ContainSingle(member => member.Email == email && member.Role == MemberRole.Admin);
        }
    }

    private sealed class CompetingInsertInterceptor : SaveChangesInterceptor
    {
        private readonly Func<Task> _competingInsert;
        private bool _hasCompeted;

        public CompetingInsertInterceptor(Func<Task> competingInsert)
        {
            _competingInsert = competingInsert;
        }

        public List<DbUpdateException> SaveFailures { get; } = [];

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_hasCompeted)
            {
                _hasCompeted = true;
                await _competingInsert();
            }

            return result;
        }

        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            if (eventData.Exception is DbUpdateException dbUpdateException)
            {
                SaveFailures.Add(dbUpdateException);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingSaveInterceptor : SaveChangesInterceptor
    {
        private readonly string _sqlState;
        private readonly string _constraintName;

        public ThrowingSaveInterceptor(string sqlState, string constraintName)
        {
            _sqlState = sqlState;
            _constraintName = constraintName;
        }

        public DbUpdateException? ThrownException { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var postgresException = new PostgresException(
                "simulated", "ERROR", "ERROR", _sqlState, constraintName: _constraintName);
            ThrownException = new DbUpdateException("simulated save failure", postgresException);
            throw ThrownException;
        }
    }
}
