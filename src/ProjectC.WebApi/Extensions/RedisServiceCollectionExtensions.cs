using Microsoft.Extensions.Options;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.PurchaseQueue;
using ProjectC.Infrastructure.Caching;
using ProjectC.Infrastructure.DistributedLocking;
using ProjectC.Infrastructure.PurchaseQueue;
using StackExchange.Redis;

namespace ProjectC.WebApi.Extensions;

public static class RedisServiceCollectionExtensions
{
    public static IServiceCollection AddRedisServices(this IServiceCollection services, IConfiguration configuration)
    {
        // DistributedLockOptions 有安全的預設值（LockTtlMultiplier = 3），比照 RateLimitingOptions
        // 不需要 ValidateOnStart（見 purchase-queue-leader-election design.md Migration Plan）。
        services
            .AddOptions<DistributedLockOptions>()
            .Bind(configuration.GetSection(DistributedLockOptions.SectionName))
            .ValidateDataAnnotations();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<DistributedLockOptions>>().Value);

        // AbortOnConnectFail MUST 為 false：StackExchange.Redis 預設值為 true，Redis 尚未就緒時
        // ConnectionMultiplexer.Connect(...) 會直接拋出例外並阻塞應用程式啟動，違反 fail-open 的
        // 降級原則（purchase-queue-leader-election design.md 決策 4／spec.md PQLE-010）。
        // 連線字串 MUST 在 factory delegate 內部才讀取 builder.Configuration（而非在這裡提前算好
        // ConfigurationOptions 再以 closure 帶入），否則會讀到 builder.Build() 之前的設定快照——
        // WebApplicationFactory 測試用的設定覆寫是在 Build() 當下才併入 builder.Configuration，
        // 提前讀取會讀到覆寫前的舊值（實測發現：PQLE-010 的啟動測試曾因此意外連到正式 redis:6379，
        // 而非測試指定的不可達 endpoint）。空字串會讓 ConfigurationOptions.Parse 產出零個 EndPoint，
        // ConnectionMultiplexer.Connect(...) 會直接同步拋出「沒有指定任何 endpoint」的例外——這與
        // AbortOnConnectFail 無關（後者只處理「endpoint 已指定但連不上」），MUST 確保永遠有一個
        // 語法合法的 endpoint 可供解析。
        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var redisConfigurationOptions = ConfigurationOptions.Parse(configuration.GetConnectionString("Redis") ?? "redis:6379");
            redisConfigurationOptions.AbortOnConnectFail = false;
            // 逾時上限明確設定，不依賴函式庫預設值（5000ms）——這是共用設定，同時套用於
            // query-caching／purchase-queue-leader-election（fail-open，更短逾時只會讓它們更快降級）
            // 與 captcha-verification（fail-closed，讓故障回應時間成為可稽核、可測試的上限）
            // （captcha-verification design.md 決策 11）。
            redisConfigurationOptions.ConnectTimeout = 2000;
            redisConfigurationOptions.SyncTimeout = 2000;
            redisConfigurationOptions.AsyncTimeout = 2000;
            redisConfigurationOptions.ConnectRetry = 1;
            return ConnectionMultiplexer.Connect(redisConfigurationOptions);
        });
        // IConnectionMultiplexer 官方建議整個應用程式共用單一實例，本身即是 thread-safe，比照既有
        // Singleton 註冊慣例（如 IMemoryCache），IDistributedLock 一併註冊為 Singleton。
        services.AddSingleton<IDistributedLock, RedisDistributedLock>();
        // 入場推進 Redis 鏡像的 best-effort 同步（PQ-JOIN／PQ-COMPLETE），比照 RedisDistributedLock
        // 無狀態、Singleton 註冊（purchase-queue-redis-admission design.md Decision 3／8）。
        services.AddSingleton<IPurchaseQueueAdmissionMirror, RedisPurchaseQueueAdmissionMirror>();

        // QueryCacheOptions：任一值缺漏或為 0／負數會讓 StackExchange.Redis 在每次快取寫入時同步拋出
        // ArgumentException，比照 PurchaseQueueOptions 啟動時 fail-fast（見 design.md 決策 6a）。
        services
            .AddOptions<QueryCacheOptions>()
            .Bind(configuration.GetSection(QueryCacheOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<QueryCacheOptions>>().Value);
        services.AddSingleton<IQueryCache, RedisQueryCache>();

        return services;
    }
}
