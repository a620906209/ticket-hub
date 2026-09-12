namespace ProjectC.Application.Common.Interfaces;

// 比照 IDistributedLock 的放置模式與介面風格：不承載業務語意，放 Application/Common/Interfaces
// 而非 Domain（見 query-caching design.md 決策 1）。契約保證：呼叫端永遠拿到未命中或正常完成，
// 不會收到 Redis／序列化例外——Infrastructure 層實作 MUST 在內部吸收所有故障，不得往外拋。
public interface IQueryCache
{
    Task<QueryCacheResult<T>> GetAsync<T>(string key, CancellationToken cancellationToken);

    Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken);

    Task RemoveAsync(string key, CancellationToken cancellationToken);
}
