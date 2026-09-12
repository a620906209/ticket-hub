using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;

namespace ProjectC.WebApi.Tests.TestSupport;

// query-caching tasks.md 第 7 節：Redis 無法連線的設置方式比照既有
// ApplicationStartupWithRedisUnavailableTests／RedisDistributedLockTests 的既定手法——指向真實、
// 語法合法但保證無人監聽的 127.0.0.1:1，比另外用 Testcontainers 啟動再關閉一個 Redis 容器更快也
// 更穩定，核心都是讓 StackExchange.Redis 走到真正的連線失敗路徑（RedisConnectionException），
// 而非人為跳過連線嘗試（CLAUDE.md Rule 11：比照既有慣例）。
// MUST 使用真正註冊的 IQueryCache（RedisQueryCache），不得以 mock/fake 取代——這裡完全不動
// DI 註冊，只覆寫連線字串，讓 GetEventsHandler/CreateEventHandler 等呼叫端 → 真正的
// RedisQueryCache（捕捉例外、記錄 Warning、回傳未命中/正常完成）→ 呼叫端照常運作的完整鏈路被真實觸發。
// 額外掛一個 ILogEventSink（比照 ObservabilityWebApplicationFactory），供測試斷言 RedisQueryCache
// 內部捕捉例外後確實記錄了 Warning 等級的結構化 log。
public sealed class RedisUnavailableWebApplicationFactory : CustomWebApplicationFactory
{
    public InMemoryLogEventSink LogSink { get; } = new();

    // 這個 factory 一律把連線字串覆寫成無法連線的 endpoint，基底類別原本會啟動的可正常運作 Redis
    // 容器完全用不到——省下這個容器，減少測試套件平行執行時的 Docker 負載。
    protected override bool NeedsWorkingRedis => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Redis"] = "127.0.0.1:1",
            });
        });

        builder.ConfigureServices(services => services.AddSingleton<ILogEventSink>(LogSink));
    }
}
