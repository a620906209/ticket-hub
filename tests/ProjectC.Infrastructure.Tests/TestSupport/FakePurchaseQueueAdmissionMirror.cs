using ProjectC.Application.Common.Interfaces;

namespace ProjectC.Infrastructure.Tests.TestSupport;

// 記錄呼叫參數與順序，供單元測試斷言 best-effort 同步呼叫發生在正確時機（例如交易 commit 之後，
// 見 OrderServiceTestFactory 的既有 FakeQueryCache 慣例）。OnSyncCompletionAsync／OnSyncJoinAsync
// 讓測試在回呼內用獨立連線查資料庫，驗證「commit 確實先於鏡像同步」。
public sealed class FakePurchaseQueueAdmissionMirror : IPurchaseQueueAdmissionMirror
{
    public List<(Guid EventId, Guid EntryId, DateTime JoinedAtUtc)> SyncJoinCalls { get; } = new();

    public List<(Guid EventId, Guid EntryId)> SyncCompletionCalls { get; } = new();

    public Func<Guid, Guid, Task>? OnSyncCompletionAsync { get; set; }

    public Func<Guid, Guid, DateTime, Task>? OnSyncJoinAsync { get; set; }

    public async Task SyncJoinAsync(Guid eventId, Guid entryId, DateTime joinedAtUtc, CancellationToken cancellationToken)
    {
        SyncJoinCalls.Add((eventId, entryId, joinedAtUtc));
        if (OnSyncJoinAsync is { } callback)
        {
            await callback(eventId, entryId, joinedAtUtc);
        }
    }

    public async Task SyncCompletionAsync(Guid eventId, Guid entryId, CancellationToken cancellationToken)
    {
        SyncCompletionCalls.Add((eventId, entryId));
        if (OnSyncCompletionAsync is { } callback)
        {
            await callback(eventId, entryId);
        }
    }
}
