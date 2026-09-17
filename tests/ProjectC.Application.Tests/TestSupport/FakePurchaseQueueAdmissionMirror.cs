using ProjectC.Application.Common.Interfaces;

namespace ProjectC.Application.Tests.TestSupport;

// 比照 ProjectC.Infrastructure.Tests 內同名類別，記錄呼叫參數供單元測試斷言（獨立測試專案，
// 不跨專案引用測試輔助類別，見既有 FakeQueryCache 慣例）。
public sealed class FakePurchaseQueueAdmissionMirror : IPurchaseQueueAdmissionMirror
{
    public List<(Guid EventId, Guid EntryId, DateTime JoinedAtUtc)> SyncJoinCalls { get; } = new();

    public List<(Guid EventId, Guid EntryId)> SyncCompletionCalls { get; } = new();

    public Task SyncJoinAsync(Guid eventId, Guid entryId, DateTime joinedAtUtc, CancellationToken cancellationToken)
    {
        SyncJoinCalls.Add((eventId, entryId, joinedAtUtc));
        return Task.CompletedTask;
    }

    public Task SyncCompletionAsync(Guid eventId, Guid entryId, CancellationToken cancellationToken)
    {
        SyncCompletionCalls.Add((eventId, entryId));
        return Task.CompletedTask;
    }
}
