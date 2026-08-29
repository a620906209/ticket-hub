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
    public const int LoginWindowSeconds = 2;

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
