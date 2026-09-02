using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace ProjectC.WebApi.Tests.TestSupport;

// 不繼承 CustomWebApplicationFactory（會連帶繼承它「建構子直接建立一個 Testcontainers 容器物件」的
// 既有行為），也不實作 IAsyncLifetime（xUnit 只對 IClassFixture<T>/ICollectionFixture<T> 建構注入的
// 物件自動呼叫 IAsyncLifetime，這個類別是測試方法內手動 new 出來的，IAsyncLifetime 不會被呼叫）。
// 每個測試方法各自 new/Dispose 一個實例，確保每個測試方法擁有獨立的 TestServer、獨立的 in-memory
// rate limiter 狀態，不受其他測試方法執行順序或殘留額度影響（login-rate-limiting design.md 決策 6）。
public class LoginRateLimitedWebApplicationFactory : WebApplicationFactory<Program>
{
    public const int LoginPermitLimit = 3;
    // 原本設為 2 秒，只在系統負載低時夠用；耗盡額度需要送出 LoginPermitLimit 次真實 HTTP+DB
    // 請求（即使平行送出，仍需至少一次請求的實際延遲），系統負載較高時（例如同時有多個
    // Testcontainers 整合測試套件在跑）單次請求延遲可能超過 2 秒，導致視窗提前重置、
    // 測試預期的限流沒有觸發（實測重現過這個 flaky 失敗）。放寬到 10 秒建立更多真實延遲餘裕，
    // 犧牲的代價只有 Login_AfterTheWindowResets_RequestsAreAllowedAgain 的 Task.Delay 變長。
    public const int LoginWindowSeconds = 10;

    private readonly string _connectionString;

    public LoginRateLimitedWebApplicationFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        TestHostConfiguration.ApplyCommonTestConfiguration(builder, _connectionString);

        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LoginRateLimiting:PermitLimit"] = LoginPermitLimit.ToString(),
                ["LoginRateLimiting:WindowSeconds"] = LoginWindowSeconds.ToString(),
            });
        });
    }
}
