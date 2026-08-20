using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            // 沒有真正的 JWT 設定來源（appsettings.json 留空），測試環境用固定、通過驗證的值覆蓋。
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Issuer"] = "ProjectC.Tests",
                ["Jwt:Audience"] = "ProjectC.Tests.Client",
                ["Jwt:SigningKey"] = "integration-test-signing-key-not-for-prod-32+",
                ["Jwt:AccessTokenExpirationMinutes"] = "30",
                ["Auth:RefreshTokenExpirationDays"] = "14",
                ["Auth:PasswordResetTokenExpirationMinutes"] = "15",
                // TicketSigningOptions 有 ValidateOnStart（見 Program.cs），appsettings.json 留空，
                // 測試環境一樣要用固定、通過驗證的值覆蓋，否則 host build 會直接失敗（比照上面的 Jwt 設定）。
                ["TicketSigning:SigningKey"] = "integration-test-ticket-signing-key-not-for-prod-32+",
            });
        });

        builder.ConfigureServices(services =>
        {
            // 移除 Program.cs 原本指向 appsettings 連線字串的 Npgsql 設定（含底層的 IDbContextOptionsConfiguration），
            // 換成指向 Testcontainers 啟動的 Postgres，否則兩份 provider 設定會同時套用而讓 EF Core 拋例外。
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ApplicationDbContext>>();
            services.RemoveAll<ApplicationDbContext>();

            services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(_connectionString));
        });
    }
}
