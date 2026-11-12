using FluentAssertions;
using ProjectC.Application.Authentication.Login;
using ProjectC.Infrastructure.Security;

namespace ProjectC.Infrastructure.Tests.Security;

public class BCryptPasswordHasherTests
{
    // dummy hash 的 cost 若與真實雜湊不同，未知 Email 與密碼錯誤的耗時會不同，等於重新開出時間側通道。
    [Fact]
    public void HashPassword_WhenCompared_HasSameCostAsLoginDummyHash()
    {
        var hasher = new BCryptPasswordHasher();

        var realHash = hasher.HashPassword("Password123");

        CostSegment(realHash).Should().Be(CostSegment(LoginHandler.DummyPasswordHash));
    }

    [Fact]
    public void VerifyPassword_WithLoginDummyHash_ReturnsFalseWithoutThrowing()
    {
        var hasher = new BCryptPasswordHasher();

        hasher.VerifyPassword("Password123", LoginHandler.DummyPasswordHash).Should().BeFalse();
    }

    private static string CostSegment(string hash) => hash.Split('$')[2];
}
