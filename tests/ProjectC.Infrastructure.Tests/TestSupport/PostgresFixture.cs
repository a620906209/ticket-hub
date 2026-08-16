using Microsoft.EntityFrameworkCore;
using ProjectC.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace ProjectC.Infrastructure.Tests.TestSupport;

// 依 CLAUDE.md 測試規範：整合測試用 Testcontainers 啟動獨立的 Postgres 容器。
// 跟 ProjectC.WebApi.Tests.TestSupport.CustomWebApplicationFactory 一樣，
// dotnet test 若在 api 容器內執行，臨時容器要掛進同一個 compose 網路才能互連
// （Docker Desktop for Windows 下，跨 bridge 網路連不到 host-published port）。
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string DatabaseName = "projectc_infra_tests";
    private const string Username = "projectc_infra_tests";
    private const string Password = "projectc_infra_tests";

    private static readonly string? ComposeNetworkName =
        Environment.GetEnvironmentVariable("Testcontainers__ComposeNetworkName");

    // 每個 fixture 實例（也就是每個共用這個 fixture 的測試 collection）用唯一別名，
    // 避免多個 collection 平行跑時搶同一個網路別名（見 CustomWebApplicationFactory 踩過的坑）。
    private readonly string _networkAlias = $"projectc-infra-test-db-{Guid.NewGuid():N}";
    private readonly PostgreSqlContainer _container;

    public string ConnectionString { get; private set; } = string.Empty;

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

        ConnectionString = ComposeNetworkName is not null
            ? $"Host={_networkAlias};Port=5432;Database={DatabaseName};Username={Username};Password={Password}"
            : _container.GetConnectionString();

        await using var dbContext = CreateDbContext();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    /// <summary>每次呼叫都回傳新的 DbContext instance，模擬獨立的資料庫連線／交易。</summary>
    public ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        return new ApplicationDbContext(options);
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
