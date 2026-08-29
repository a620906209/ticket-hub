using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ProjectC.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace ProjectC.WebApi.Tests.TestSupport;

// 依 CLAUDE.md 測試規範：整合測試用 Testcontainers 啟動獨立的 Postgres 容器，
// 不連線開發用的 `db` compose 服務，確保測試互相隔離、也不會污染開發資料。
public class CustomWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string TestDatabaseName = "projectc_tests";
    private const string TestDatabaseUsername = "projectc_tests";
    private const string TestDatabasePassword = "projectc_tests";

    // dotnet test 本身若是在 api 容器內執行（見 docker-compose.yml），Testcontainers 建立的臨時
    // Postgres 容器必須掛進 api 所在的 compose 網路，改用網路別名直接互連；否則 Docker Desktop for
    // Windows 下，容器對外開的 host port 無法跨 bridge 網路連線（host-published port 只在同網路內可達）。
    // 用既有網路名稱參照（WithNetwork(string name)），不透過 NetworkBuilder 建立，
    // 否則對已存在的 compose 網路會撞名衝突。
    private static readonly string? ComposeNetworkName =
        Environment.GetEnvironmentVariable("Testcontainers__ComposeNetworkName");

    // xUnit 預設不同測試類別會平行跑，每個都會建立自己的 CustomWebApplicationFactory；
    // 別名必須每個實例唯一，否則多個容器搶同一個網路別名，連線會被導到別的測試類別的資料庫容器。
    private readonly string _testDatabaseNetworkAlias = $"projectc-test-db-{Guid.NewGuid():N}";

    private readonly PostgreSqlContainer _dbContainer;
    private string _connectionString = string.Empty;

    public CustomWebApplicationFactory()
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

        _connectionString = connectionString;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _dbContainer.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        TestHostConfiguration.ApplyCommonTestConfiguration(builder, _connectionString);

        // 一旦 Program.cs 加上全域的 "login" policy，所有使用這個 base class（或其子類別）的既有整合
        // 測試都會受影響（不只 OrdersRateLimitingTests）——AuthControllerTests 單一測試類別內就有十幾個
        // 呼叫登入 API 的測試方法，累計次數遠超過 production 預設的 PermitLimit = 5/WindowSeconds = 60。
        // 這裡用寬鬆額度覆寫，確保這些測試的正常登入流程不會被登入限流誤傷（login-rate-limiting
        // design.md 決策 6 問題 A、點 1）。時間窗沿用與 production 相同的 60 秒量級，只放大額度。
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LoginRateLimiting:PermitLimit"] = "1000",
                ["LoginRateLimiting:WindowSeconds"] = "60",
            });
        });
    }
}
