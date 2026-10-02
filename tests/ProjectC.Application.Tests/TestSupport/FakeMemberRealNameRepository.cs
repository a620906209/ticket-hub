using ProjectC.Domain.Members;

namespace ProjectC.Application.Tests.TestSupport;

/// <summary>記錄呼叫次數、Id 與 token，供「不需實名不查詢」「每次請求最多查一次」「token 原樣傳遞」的斷言使用
/// （real-name-verification tasks.md 3.12）。</summary>
public sealed class FakeMemberRealNameRepository : IMemberRealNameRepository
{
    public Dictionary<Guid, MemberRealName> Data { get; } = new();

    /// <summary>設為 false 以模擬條件式 UPDATE 被並發請求搶先（affected rows = 0）。</summary>
    public bool TryRegisterResult { get; set; } = true;

    public int TryRegisterCallCount { get; private set; }
    public Guid? LastTryRegisterMemberId { get; private set; }
    public CancellationToken? LastTryRegisterToken { get; private set; }
    public int GetCallCount { get; private set; }
    public Guid? LastGetMemberId { get; private set; }
    public CancellationToken? LastGetToken { get; private set; }

    public Task<bool> TryRegisterAsync(Guid memberId, string realName, string nationalIdLast4, CancellationToken cancellationToken)
    {
        TryRegisterCallCount++;
        LastTryRegisterMemberId = memberId;
        LastTryRegisterToken = cancellationToken;
        if (!TryRegisterResult || !Data.TryAdd(memberId, new MemberRealName(realName, nationalIdLast4)))
            return Task.FromResult(false);

        return Task.FromResult(true);
    }

    public Task<MemberRealName?> GetAsync(Guid memberId, CancellationToken cancellationToken)
    {
        GetCallCount++;
        LastGetMemberId = memberId;
        LastGetToken = cancellationToken;
        return Task.FromResult(Data.GetValueOrDefault(memberId));
    }
}
