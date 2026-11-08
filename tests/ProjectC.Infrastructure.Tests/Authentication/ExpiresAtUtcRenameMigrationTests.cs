using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ProjectC.Domain.Members;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests.Authentication;

/// <summary>
/// time-field-naming-rule tasks.md 3.1：實際執行改名 migration 的 Up／Down。結構比照 OrderCreatedAtBackfillMigrationTests：
/// 每個測試建立自己的獨立資料庫；停在前一支 migration 時目前的 EF model 已是新欄位名，所以只能以參數化原生 SQL 讀寫 Token 表。
/// </summary>
[Collection(PostgresCollection.Name)]
public class ExpiresAtUtcRenameMigrationTests
{
    private const string MigrationBeforeRename = "20261014081305_AddOrderCreatedAt";
    private const string RenameMigration = "RenameTokenExpiresAtToExpiresAtUtc";

    private static readonly DateTime RefreshExpiry = new(2026, 12, 1, 8, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime ResetExpiry = new(2026, 12, 2, 9, 45, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _fixture;

    public ExpiresAtUtcRenameMigrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

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

    private static async Task SeedTokensAsync(ApplicationDbContext dbContext)
    {
        var member = Member.Register($"legacy-{Guid.NewGuid():N}@example.com", "Legacy Member", "hash");
        dbContext.Members.Add(member);
        await dbContext.SaveChangesAsync();

        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "RefreshTokens" ("Id", "MemberId", "TokenHash", "Status", "ExpiresAt") VALUES ({Guid.NewGuid()}, {member.Id}, 'refresh-hash', 0, {RefreshExpiry})""");
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "PasswordResetTokens" ("Id", "MemberId", "TokenHash", "ExpiresAt", "IsUsed") VALUES ({Guid.NewGuid()}, {member.Id}, 'reset-hash', {ResetExpiry}, false)""");
    }

    private static Task<List<string>> ReadExpiryColumnNamesAsync(ApplicationDbContext dbContext, string tableName)
        => dbContext.Database.SqlQuery<string>($"""
            SELECT column_name AS "Value" FROM information_schema.columns
            WHERE table_name = {tableName} AND column_name LIKE 'ExpiresAt%'
            ORDER BY column_name
            """).ToListAsync();

    private static Task<DateTime> ReadExpiryValueAsync(ApplicationDbContext dbContext, string tableName, string columnName)
    {
        // 表名與欄位名皆為呼叫端寫死的常數，不含外部輸入；識別字不能參數化。
        #pragma warning disable EF1002 // 理由同上方註解
        return dbContext.Database.SqlQueryRaw<DateTime>($"""SELECT "{columnName}" AS "Value" FROM "{tableName}" """).SingleAsync();
        #pragma warning restore EF1002
    }

    // 改名保留資料：遷移後欄位名為 ExpiresAtUtc、舊欄位消失、既有 Token 的到期時間不變（使用者不必重新登入）。
    [Theory]
    [InlineData("RefreshTokens")]
    [InlineData("PasswordResetTokens")]
    public async Task Up_WhenTokensExist_RenamesColumnAndKeepsValue(string tableName)
    {
        await using var dbContext = await CreateDatabaseAsync();
        await MigrateToAsync(dbContext, MigrationBeforeRename);
        await SeedTokensAsync(dbContext);
        var expected = tableName == "RefreshTokens" ? RefreshExpiry : ResetExpiry;

        await MigrateToAsync(dbContext, RenameMigration);

        (await ReadExpiryColumnNamesAsync(dbContext, tableName)).Should().Equal("ExpiresAtUtc");
        (await ReadExpiryValueAsync(dbContext, tableName, "ExpiresAtUtc")).Should().Be(expected);
    }

    // 回滾路徑：Down 把欄位名還原為 ExpiresAt，值不變。
    [Theory]
    [InlineData("RefreshTokens")]
    [InlineData("PasswordResetTokens")]
    public async Task Down_AfterUp_RestoresOldColumnNameAndKeepsValue(string tableName)
    {
        await using var dbContext = await CreateDatabaseAsync();
        await MigrateToAsync(dbContext, MigrationBeforeRename);
        await SeedTokensAsync(dbContext);
        await MigrateToAsync(dbContext, RenameMigration);
        var expected = tableName == "RefreshTokens" ? RefreshExpiry : ResetExpiry;

        await MigrateToAsync(dbContext, MigrationBeforeRename);

        (await ReadExpiryColumnNamesAsync(dbContext, tableName)).Should().Equal("ExpiresAt");
        (await ReadExpiryValueAsync(dbContext, tableName, "ExpiresAt")).Should().Be(expected);
    }
}
