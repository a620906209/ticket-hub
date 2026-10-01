using ProjectC.Domain.Members;

namespace ProjectC.Application.Tests.TestSupport;

public sealed class FakeMemberDisplayNameReader : IMemberDisplayNameReader
{
    public Dictionary<Guid, string> DisplayNamesByMemberId { get; } = new();

    public int GetDisplayNamesByIdsCallCount { get; private set; }
    public IReadOnlyList<Guid>? LastGetDisplayNamesByIdsIds { get; private set; }
    public CancellationToken? LastGetDisplayNamesByIdsToken { get; private set; }

    public Task<IReadOnlyDictionary<Guid, string>> GetDisplayNamesByIdsAsync(IReadOnlyList<Guid> memberIds, CancellationToken cancellationToken)
    {
        GetDisplayNamesByIdsCallCount++;
        LastGetDisplayNamesByIdsIds = memberIds.ToList();
        LastGetDisplayNamesByIdsToken = cancellationToken;
        return Task.FromResult<IReadOnlyDictionary<Guid, string>>(DisplayNamesByMemberId
            .Where(pair => memberIds.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value));
    }
}
