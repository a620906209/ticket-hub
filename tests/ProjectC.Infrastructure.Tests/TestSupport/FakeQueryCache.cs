using ProjectC.Application.Common.Interfaces;

namespace ProjectC.Infrastructure.Tests.TestSupport;

// 真的會儲存/刪除資料的 Dictionary 實作，不是只記錄呼叫次數而不改變狀態的 mock（query-caching
// tasks.md 5.5／5.6a／5.6b／5.7 等整合測試的既定手法）；同時記錄每個方法的呼叫參數，
// 供單元測試做 Verify 風格斷言。比照 ProjectC.Application.Tests 內同名類別，但這裡是獨立的測試
// 專案，不跨專案引用測試輔助類別（見 OrderServiceNotificationTests 既有的 Spy 慣例）。
public sealed class FakeQueryCache : IQueryCache
{
    private readonly Dictionary<string, object?> _store = new();

    public List<string> GetCalls { get; } = new();

    public List<(string Key, object? Value, TimeSpan Ttl)> SetCalls { get; } = new();

    public List<string> RemoveCalls { get; } = new();

    /// <summary>在 RemoveAsync 實際執行的當下觸發，供測試在回呼內用獨立連線查資料庫，驗證「commit 確實
    /// 先於 invalidation」（見 tasks.md 第 4、5 節開頭的回呼手法）。</summary>
    public Func<string, Task>? OnRemoveAsync { get; set; }

    public void Seed<T>(string key, T value) => _store[key] = value;

    public bool ContainsKey(string key) => _store.ContainsKey(key);

    public Task<QueryCacheResult<T>> GetAsync<T>(string key, CancellationToken cancellationToken)
    {
        GetCalls.Add(key);
        if (_store.TryGetValue(key, out var value) && value is T typed)
        {
            return Task.FromResult(QueryCacheResult<T>.Hit(typed));
        }

        return Task.FromResult(QueryCacheResult<T>.Miss());
    }

    public Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken)
    {
        SetCalls.Add((key, value, ttl));
        _store[key] = value;
        return Task.CompletedTask;
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        RemoveCalls.Add(key);
        _store.Remove(key);
        if (OnRemoveAsync is { } callback)
        {
            await callback(key);
        }
    }
}
