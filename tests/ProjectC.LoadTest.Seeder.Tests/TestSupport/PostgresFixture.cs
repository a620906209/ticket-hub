using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProjectC.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace ProjectC.LoadTest.Seeder.Tests.TestSupport;

// 依 CLAUDE.md 測試規範：整合測試用 Testcontainers 啟動獨立的 Postgres 容器，不連開發用 db。
// 在 api 容器內執行時，臨時容器要掛進同一個 compose 網路才能互連（同 ProjectC.Infrastructure.Tests.TestSupport.PostgresFixture）。
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string DatabaseName = "projectc_loadtest_seeder_tests";
    private const string Username = "projectc_loadtest_seeder_tests";
    private const string Password = "projectc_loadtest_seeder_tests";

    private static readonly string? ComposeNetworkName =
        Environment.GetEnvironmentVariable("Testcontainers__ComposeNetworkName");

    // DNS label 上限 63 字元，前綴加 32 位 GUID 不能超過，否則別名無法解析。
    private readonly string _networkAlias = $"loadtest-seeder-db-{Guid.NewGuid():N}";
    private readonly PostgreSqlContainer _container;

    private string _serverConnectionString = string.Empty;

    public PostgresFixture()
    {
        var builder = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase(DatabaseName)
            .WithUsername(Username)
            .WithPassword(Password);

        if (ComposeNetworkName is not null)
            builder = builder.WithNetwork(ComposeNetworkName).WithNetworkAliases(_networkAlias);

        _container = builder.Build();
    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        _serverConnectionString = ComposeNetworkName is not null
            ? $"Host={_networkAlias};Port=5432;Database={DatabaseName};Username={Username};Password={Password}"
            : _container.GetConnectionString();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    /// <summary>
    /// 每個測試一個獨立資料庫：壓測 Admin email 是固定常數，共用同一個資料庫會讓測試互相影響。
    /// </summary>
    public async Task<string> CreateDatabaseAsync(bool applyMigrations)
    {
        var databaseName = $"seeder_{Guid.NewGuid():N}";
        await using (var serverContext = CreateDbContext(_serverConnectionString))
        {
            // 資料庫名稱是測試自己產生的 GUID，不含外部輸入；CREATE DATABASE 不支援參數化識別字。
            await serverContext.Database.ExecuteSqlRawAsync($"CREATE DATABASE \"{databaseName}\"");
        }

        var connectionString = new NpgsqlConnectionStringBuilder(_serverConnectionString) { Database = databaseName }.ConnectionString;
        if (applyMigrations)
        {
            await using var dbContext = CreateDbContext(connectionString);
            await dbContext.Database.MigrateAsync();
        }

        return connectionString;
    }

    public static ApplicationDbContext CreateDbContext(string connectionString)
        => new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connectionString).Options);
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
