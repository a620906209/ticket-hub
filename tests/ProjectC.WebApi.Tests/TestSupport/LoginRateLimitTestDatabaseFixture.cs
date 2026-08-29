using Microsoft.EntityFrameworkCore;
using ProjectC.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace ProjectC.WebApi.Tests.TestSupport;

// 只負責啟動/停止一個共用的 Testcontainers Postgres 容器並跑 migration，不含任何限流器狀態——
// 純粹是資料庫連線的共用資源，讓 LoginRateLimitingTests 內每個測試方法各自 new 一個全新的
// LoginRateLimitedWebApplicationFactory 時，不用每個測試方法都重新啟動一次容器
// （login-rate-limiting design.md 決策 6 點 3）。
public class LoginRateLimitTestDatabaseFixture : IAsyncLifetime
{
    private const string TestDatabaseName = "projectc_tests";
    private const string TestDatabaseUsername = "projectc_tests";
    private const string TestDatabasePassword = "projectc_tests";

    private static readonly string? ComposeNetworkName =
        Environment.GetEnvironmentVariable("Testcontainers__ComposeNetworkName");

    private readonly string _testDatabaseNetworkAlias = $"projectc-test-db-{Guid.NewGuid():N}";

    private readonly PostgreSqlContainer _dbContainer;

    public string ConnectionString { get; private set; } = string.Empty;

    public LoginRateLimitTestDatabaseFixture()
    {
        var builder = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase(TestDatabaseName)
            .WithUsername(TestDatabaseUsername)
            .WithPassword(TestDatabasePassword);

        if (ComposeNetworkName is not null)
        {
            builder = builder.WithNetwork(ComposeNetworkName).WithNetworkAliases(_testDatabaseNetworkAlias);
        }

        _dbContainer = builder.Build();
    }

    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();

        var connectionString = ComposeNetworkName is not null
            ? $"Host={_testDatabaseNetworkAlias};Port=5432;Database={TestDatabaseName};Username={TestDatabaseUsername};Password={TestDatabasePassword}"
            : _dbContainer.GetConnectionString();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        await using var dbContext = new ApplicationDbContext(options);
        await dbContext.Database.MigrateAsync();

        ConnectionString = connectionString;
    }

    public async Task DisposeAsync()
    {
        await _dbContainer.DisposeAsync();
    }
}
