using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.WebApi.Tests.TestSupport;
using StackExchange.Redis;

namespace ProjectC.WebApi.Tests.Startup;

// purchase-queue-leader-election spec.md PQLE-010（tasks.md 5.5）：應用程式啟動時 Redis 不可用，
// 範圍僅限「Host 啟動不被阻塞」——AdvanceQueueOnceWithLeaderElectionAsync 在 Redis 不可用時的
// fail-open 執行行為已由 PurchaseQueueAdmissionServiceLeaderElectionBranchTests／
// PurchaseQueueAdmissionServiceLeaderElectionTests（PQLE-007）涵蓋，這裡不重複測試，也不需要
// PurchaseQueueAdmissionService 實際跑起來——Testing 環境依既有慣例本來就不會註冊它
// （見 design.md 第 12 行），與本測試的斷言範圍無關。
public class ApplicationStartupWithRedisUnavailableTests
{
    // 指向真實、語法合法但保證連不上的 endpoint（同一容器內沒有任何服務監聽 port 1）——比起另外用
    // Testcontainers 啟動再關閉一個 Redis 容器，這個位址一樣是「真實會嘗試連線、真實連線失敗」，
    // 但不受 Docker port 分配／容器啟停時序影響，更快也更穩定；核心都是讓 StackExchange.Redis
    // 走到真正的連線失敗路徑，而非人為跳過連線嘗試。
    private const string UnreachableRedisEndpoint = "127.0.0.1:1";

    // Postgres 連線字串刻意也給一個語法合法但同樣連不上的值：本測試只驗證 Host 建置／啟動階段
    // 是否成功，不需要真的查詢資料庫（EF Core 的 UseNpgsql 不會在註冊當下立即建立連線）；
    // 使用 compose service name（db）而非 localhost，符合 CLAUDE.md 連線字串慣例。
    private const string UnusedPostgresConnectionString =
        "Host=db;Port=5432;Database=projectc_pqle_startup_unused;Username=unused;Password=unused";

    private sealed class RedisUnavailableAtStartupFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // MUST 保留 Program.cs 真正的 IConnectionMultiplexer／IDistributedLock 註冊路徑
            // ——不得用 ConfigureTestServices 移除或替換成 mock，否則無法證明 PQLE-010。
            TestHostConfiguration.ApplyCommonTestConfiguration(builder, UnusedPostgresConnectionString);

            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Redis"] = UnreachableRedisEndpoint,
                });
            });
        }
    }

    // 合併為單一測試：分開驗證「Host 能啟動」與「DI 有建立 IConnectionMultiplexer」不足以證明
    // PQLE-010——必須在同一次啟動中，明確解析正式 DI 容器（而非另外建構或 mock）的
    // IConnectionMultiplexer，且確認 Host 在那之後仍能繼續處理請求，才能同時證明「DI 容器建置與
    // 連線建立不阻塞啟動」與「連線失敗不影響 Host 之後的可運作狀態」兩件事。
    [Fact]
    public async Task ApplicationHost_WhenRedisUnreachableAtStartup_StartsAndKeepsServingWhileConnectionMultiplexerReflectsGenuineFailure()
    {
        using var factory = new RedisUnavailableAtStartupFactory();

        // CreateClient() 內部會觸發真正的 Host 建置（含 Program.cs 的 IConnectionMultiplexer.Connect(...)
        // 呼叫）；若 AbortOnConnectFail 沒有正確設為 false，這裡就會直接拋出例外，測試會失敗在這一行。
        using var client = factory.CreateClient();
        var responseBeforeResolvingMultiplexer = await client.GetAsync("/api/events");
        responseBeforeResolvingMultiplexer.Should().NotBeNull("Host 建置與啟動流程必須成功完成，DI 容器建置與 Redis 連線建立不得拋出例外中止啟動");

        // MUST 從正式 DI 容器（factory.Services，即 Program.cs 實際建置出來的服務提供者）解析，
        // 不得另外 new 一個 ConnectionMultiplexer 來斷言——否則無法證明「正式的 IConnectionMultiplexer
        // 註冊確實被建立」，只證明了「StackExchange.Redis 這個 API 本身在某個連不上的位址下不會拋例外」。
        var connectionMultiplexer = factory.Services.GetRequiredService<IConnectionMultiplexer>();
        connectionMultiplexer.IsConnected.Should().BeFalse(
            "指向的 endpoint 保證連不上，MUST 觀察到連線初期確實失敗，而不是「根本沒有嘗試連線」");

        // 解析出一個處於「連不上」狀態的 IConnectionMultiplexer 之後，Host 仍須能繼續正常處理請求
        // ——證明 Redis 連線失敗除了讓這個 Singleton 本身處於未連線狀態外，不會讓 Host 進入不可用狀態。
        var responseAfterResolvingMultiplexer = await client.GetAsync("/api/events");
        responseAfterResolvingMultiplexer.Should().NotBeNull("解析出未連線的 IConnectionMultiplexer 之後，Host 仍必須處於可運作狀態，能繼續回應請求");
    }
}
