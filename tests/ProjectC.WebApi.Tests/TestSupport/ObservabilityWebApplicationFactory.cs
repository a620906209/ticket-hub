using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Infrastructure.Persistence;
using Serilog.Core;
using Testcontainers.PostgreSql;

namespace ProjectC.WebApi.Tests.TestSupport;

// 比照 CustomWebApplicationFactory 用 Testcontainers 啟動獨立 Postgres（不連開發用的 db 服務）；
// 額外透過 DI 註冊一個 ILogEventSink（InMemoryLogEventSink），讓 Program.cs 唯一一次的
// UseSerilog（SerilogConfigurator.Configure）從 IServiceProvider 解析並掛上，供測試斷言實際
// 輸出的 LogEvent 結構化屬性；並可選擇性覆寫 Seq:ServerUrl 用於 Seq sink 連線失敗容錯測試
// （observability tasks.md 4.1、5.12、5.13）。
public sealed class ObservabilityWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string TestDatabaseName = "projectc_tests";
    private const string TestDatabaseUsername = "projectc_tests";
    private const string TestDatabasePassword = "projectc_tests";

    private static readonly string? ComposeNetworkName =
        Environment.GetEnvironmentVariable("Testcontainers__ComposeNetworkName");

    private readonly string _testDatabaseNetworkAlias = $"projectc-test-db-{Guid.NewGuid():N}";
    private readonly PostgreSqlContainer _dbContainer;
    private string _connectionString = string.Empty;

    public InMemoryLogEventSink LogSink { get; } = new();

    /// <summary>
    /// 覆寫 "Seq:ServerUrl" 設定值；<see langword="null"/>（預設）時沿用 appsettings 預設（空值，
    /// Seq sink 不啟用）。MUST 在 <see cref="InitializeAsync"/> 或第一次存取 <c>Server</c>／
    /// <c>Services</c>／<c>CreateClient()</c> 之前設定才會生效（host 尚未建置前都可修改）。
    /// xUnit 的 IClassFixture&lt;T&gt; 只接受真正零參數的建構子，故用可寫屬性而非建構子參數傳遞
    /// （5.12／5.13 直接 new 這個 factory、設定此屬性後再用，不透過 IClassFixture 共用）。
    /// </summary>
    public string? SeqServerUrl { get; set; }

    /// <summary>
    /// 覆寫 "LoginRateLimiting:PermitLimit"／"LoginRateLimiting:WindowSeconds"。預設沿用刻意調低的
    /// 3／2（見下方 ConfigureWebHost 註解，RequestTraceIdTests 需要這個緊縮視窗才能在暖機階段打滿
    /// 額度觸發 429）。像 BackgroundServiceTraceIdTests 這種完全不測試限流行為、卻在單一測試方法內
    /// 密集呼叫多次登入 API 的測試類別，應該把這兩個值改寬，避免在系統負載較高、實際呼叫時序被拉開
    /// 或壓縮時意外撞上這個原本是為了「刻意」觸發 429 而設的緊縮視窗（見 strict-reviewer 在
    /// query-caching 變更審查中發現：新增大量測試推高整體 Docker/HTTP 負載後，這個共用緊縮視窗讓
    /// 完全不相關的測試變得 flaky）。MUST 在 <see cref="InitializeAsync"/> 或第一次存取
    /// <c>Server</c>／<c>Services</c>／<c>CreateClient()</c> 之前設定才會生效，比照 <see cref="SeqServerUrl"/>。
    /// </summary>
    public int LoginRateLimitPermitLimit { get; set; } = 3;

    public int LoginRateLimitWindowSeconds { get; set; } = 2;

    public ObservabilityWebApplicationFactory()
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

        // 刻意調低登入限流額度（比照 LoginRateLimitedWebApplicationFactory 的既有慣例），讓
        // OBS-REQUEST-TRACE-CONSISTENT 測試能少量請求內就觸發 429，藉此讀到 ProblemDetails 裡的
        // traceId 與 LogSink 記錄的 TraceId 屬性比對——429 走既有 OnRejected 分支而非例外，
        // 用它驗證比另外自造一個會拋例外的路徑更貼近既有程式碼行為。
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LoginRateLimiting:PermitLimit"] = LoginRateLimitPermitLimit.ToString(),
                ["LoginRateLimiting:WindowSeconds"] = LoginRateLimitWindowSeconds.ToString(),
            });
        });

        if (SeqServerUrl is not null)
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Seq:ServerUrl"] = SeqServerUrl,
                });
            });
        }

        // Program.cs 的 SerilogConfigurator.Configure 會從 services 解析 ILogEventSink 並掛上——
        // 這裡註冊即可，不需要（也不應該）再呼叫一次 UseSerilog（見上方類別註解）。
        builder.ConfigureServices(services => services.AddSingleton<ILogEventSink>(LogSink));
    }
}
