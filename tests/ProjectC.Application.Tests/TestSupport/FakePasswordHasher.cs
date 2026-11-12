using ProjectC.Application.Common.Interfaces;

namespace ProjectC.Application.Tests.TestSupport;

public sealed class FakePasswordHasher : IPasswordHasher
{
    public string HashPassword(string password) => $"hashed:{password}";

    public int VerifyPasswordCallCount { get; private set; }

    public bool VerifyPassword(string password, string passwordHash)
    {
        VerifyPasswordCallCount++;
        return passwordHash == HashPassword(password);
    }
}
