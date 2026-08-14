using FluentAssertions;
using ProjectC.Domain.Members;

namespace ProjectC.Domain.Tests.Members;

public class MemberTests
{
    [Fact]
    public void Register_WithValidData_CreatesActiveMemberWithMemberRole()
    {
        var member = Member.Register("user@example.com", "Alice", "hashed-password");

        member.Email.Should().Be("user@example.com");
        member.DisplayName.Should().Be("Alice");
        member.PasswordHash.Should().Be("hashed-password");
        member.Role.Should().Be(MemberRole.Member);
        member.IsActive.Should().BeTrue();
    }

    [Fact]
    public void ChangeDisplayName_WhenCalled_UpdatesDisplayNameOnly()
    {
        var member = Member.Register("user@example.com", "Alice", "hashed-password");

        member.ChangeDisplayName("Alice Chen");

        member.DisplayName.Should().Be("Alice Chen");
        member.Role.Should().Be(MemberRole.Member);
        member.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Deactivate_WhenCalled_SetsIsActiveFalse()
    {
        var member = Member.Register("user@example.com", "Alice", "hashed-password");

        member.Deactivate();

        member.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Activate_AfterDeactivate_SetsIsActiveTrueAgain()
    {
        var member = Member.Register("user@example.com", "Alice", "hashed-password");
        member.Deactivate();

        member.Activate();

        member.IsActive.Should().BeTrue();
    }
}
