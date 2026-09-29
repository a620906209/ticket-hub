using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Members;

namespace ProjectC.Application.Tests.TestSupport;

public sealed class FakeTokenService : ITokenService
{
    private int _opaqueTokenCounter;

    public string GenerateAccessToken(Member member, Guid? organizerId = null)
        => organizerId is { } value ? $"access-token:{member.Id}:org:{value}" : $"access-token:{member.Id}";

    public string GenerateOpaqueToken() => $"opaque-token-{Interlocked.Increment(ref _opaqueTokenCounter)}";

    public string HashOpaqueToken(string token) => $"hash:{token}";
}
