using FluentAssertions;
using ProjectC.Domain.Authentication;

namespace ProjectC.Domain.Tests.Authentication;

public class RefreshTokenTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Issue_WhenCalled_CreatesActiveToken()
    {
        var token = RefreshToken.Issue(Guid.NewGuid(), "hash", Now.AddDays(14));

        token.Status.Should().Be(RefreshTokenStatus.Active);
        token.IsActive(Now).Should().BeTrue();
    }

    [Fact]
    public void MarkAsUsed_WhenActive_TransitionsToUsed()
    {
        var token = RefreshToken.Issue(Guid.NewGuid(), "hash", Now.AddDays(14));

        token.MarkAsUsed();

        token.Status.Should().Be(RefreshTokenStatus.Used);
    }

    [Fact]
    public void MarkAsUsed_WhenAlreadyUsed_ThrowsInvalidOperationException()
    {
        var token = RefreshToken.Issue(Guid.NewGuid(), "hash", Now.AddDays(14));
        token.MarkAsUsed();

        var act = () => token.MarkAsUsed();

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkAsUsed_WhenRevoked_ThrowsInvalidOperationException()
    {
        var token = RefreshToken.Issue(Guid.NewGuid(), "hash", Now.AddDays(14));
        token.Revoke();

        var act = () => token.MarkAsUsed();

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Revoke_WhenActive_TransitionsToRevoked()
    {
        var token = RefreshToken.Issue(Guid.NewGuid(), "hash", Now.AddDays(14));

        token.Revoke();

        token.Status.Should().Be(RefreshTokenStatus.Revoked);
    }

    [Fact]
    public void Revoke_WhenAlreadyRevoked_StaysRevokedWithoutThrowing()
    {
        var token = RefreshToken.Issue(Guid.NewGuid(), "hash", Now.AddDays(14));
        token.Revoke();

        var act = () => token.Revoke();

        act.Should().NotThrow();
        token.Status.Should().Be(RefreshTokenStatus.Revoked);
    }

    [Fact]
    public void IsActive_WhenExpired_ReturnsFalseEvenIfStatusIsActive()
    {
        var token = RefreshToken.Issue(Guid.NewGuid(), "hash", Now.AddMinutes(-1));

        token.IsActive(Now).Should().BeFalse();
    }

    [Fact]
    public void IsActive_WhenUsed_ReturnsFalse()
    {
        var token = RefreshToken.Issue(Guid.NewGuid(), "hash", Now.AddDays(14));
        token.MarkAsUsed();

        token.IsActive(Now).Should().BeFalse();
    }
}
