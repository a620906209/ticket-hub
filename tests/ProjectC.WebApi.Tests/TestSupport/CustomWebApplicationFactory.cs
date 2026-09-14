using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Infrastructure.Persistence;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

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
    private readonly string _testRedisNetworkAlias = $"projectc-test-redis-{Guid.NewGuid():N}";

    private readonly PostgreSqlContainer _dbContainer;
    private readonly RedisContainer? _redisContainer;
    private string _connectionString = string.Empty;
    private string _redisConnectionString = string.Empty;

    // 子類別若會在 ConfigureWebHost 內把 ConnectionStrings:Redis 覆寫成別的位址（例如刻意無法連線的
    // endpoint，見 RedisUnavailableWebApplicationFactory），這個容器啟動了也用不到——覆寫為 false
    // 省下一個完全浪費的 Testcontainers 容器，減少整體測試套件平行執行時的 Docker 負載
    // （strict-reviewer 發現大量新增的 query-caching 測試推高了 Docker 負載，讓既有
    // PurchaseQueueAdmissionServiceLeaderElectionTests 這類本已對排程延遲敏感的計時測試更容易 flaky）。
    protected virtual bool NeedsWorkingRedis => true;

    public CustomWebApplicationFactory()
    {
        var dbBuilder = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase(TestDatabaseName)
            .WithUsername(TestDatabaseUsername)
            .WithPassword(TestDatabasePassword);

        if (ComposeNetworkName is not null)
        {
            dbBuilder = dbBuilder.WithNetwork(ComposeNetworkName).WithNetworkAliases(_testDatabaseNetworkAlias);
        }

        _dbContainer = dbBuilder.Build();

        if (NeedsWorkingRedis)
        {
            var redisBuilder = new RedisBuilder("redis:7-alpine");
            if (ComposeNetworkName is not null)
            {
                redisBuilder = redisBuilder.WithNetwork(ComposeNetworkName).WithNetworkAliases(_testRedisNetworkAlias);
            }

            _redisContainer = redisBuilder.Build();
        }
    }

    public async Task InitializeAsync()
    {
        if (_redisContainer is not null)
        {
            await Task.WhenAll(_dbContainer.StartAsync(), _redisContainer.StartAsync());
        }
        else
        {
            await _dbContainer.StartAsync();
        }

        var connectionString = ComposeNetworkName is not null
            ? $"Host={_testDatabaseNetworkAlias};Port=5432;Database={TestDatabaseName};Username={TestDatabaseUsername};Password={TestDatabasePassword}"
            : _dbContainer.GetConnectionString();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        await using var dbContext = new ApplicationDbContext(options);
        await dbContext.Database.MigrateAsync();

        _connectionString = connectionString;
        // 比照 Postgres：每個測試類別各自的 CustomWebApplicationFactory 都要有獨立的 Redis，不連線
        // 開發用的 `redis` compose 服務——query-caching 導入固定、非隨機的快取 key（如
        // query-cache:events:list），若共用同一個 Redis 實例，不同測試類別／測試方法之間會透過殘留的
        // 快取內容互相污染（見 query-caching design.md 決策 3）。
        if (_redisContainer is not null)
        {
            _redisConnectionString = ComposeNetworkName is not null
                ? $"{_testRedisNetworkAlias}:6379"
                : _redisContainer.GetConnectionString();
        }
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (_redisContainer is not null)
        {
            await Task.WhenAll(_dbContainer.DisposeAsync().AsTask(), _redisContainer.DisposeAsync().AsTask());
        }
        else
        {
            await _dbContainer.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        TestHostConfiguration.ApplyCommonTestConfiguration(builder, _connectionString);

        if (_redisContainer is not null)
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Redis"] = _redisConnectionString,
                });
            });
        }

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

        // 預設把 ICaptchaService 換成固定 token／答案的 FakeCaptchaService：這個基底類別（含子類別）
        // 共 20 個既有測試檔案透過 AuthTestHelper／直接建構 LoginRequest／RegisterMemberRequest 呼叫
        // 真實的註冊／登入端點準備測試前置資料，本身完全不是在測驗證碼行為，也沒有任何方式能程式化
        // 解出真實 RedisCaptchaService 產生的隨機圖形驗證碼內容（captcha-verification design.md 決策 9）。
        // 需要驗證真實 RedisCaptchaService 行為的測試類別（CAPTCHA-GEN-*／CAPTCHA-RATE-001／
        // CAPTCHA-FAIL-*）MUST 個別在自己的 ConfigureTestServices 覆寫換回真實服務，比照
        // NeedsWorkingRedis 這種既有透過子類別覆寫改變基底行為的既定慣例。
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICaptchaService>();
            services.AddScoped<ICaptchaService, FakeCaptchaService>();
        });
    }
}
