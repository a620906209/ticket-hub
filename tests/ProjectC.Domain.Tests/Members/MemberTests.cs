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

    [Fact]
    public void RegisterRealName_WhenNotRegistered_SetsBothFields()
    {
        var member = Member.Register("user@example.com", "Alice", "hashed-password");

        var isRegistered = member.RegisterRealName("王小明", "1234");

        isRegistered.Should().BeTrue();
        member.RealName.Should().Be("王小明");
        member.NationalIdLast4.Should().Be("1234");
        member.HasRegisteredRealName.Should().BeTrue();
    }

    // 登記後不可變更是防止「改實名」繞過實名轉賣的核心規則，同值重送也不算成功。
    [Theory]
    [InlineData("李大華", "5678")]
    [InlineData("王小明", "1234")]
    public void RegisterRealName_WhenAlreadyRegistered_ReturnsFailureAndKeepsOriginalValues(string realName, string nationalIdLast4)
    {
        var member = Member.Register("user@example.com", "Alice", "hashed-password");
        member.RegisterRealName("王小明", "1234");

        var isRegistered = member.RegisterRealName(realName, nationalIdLast4);

        isRegistered.Should().BeFalse();
        member.RealName.Should().Be("王小明");
        member.NationalIdLast4.Should().Be("1234");
    }

    [Theory]
    [InlineData(null, "1234")]
    [InlineData("", "1234")]
    [InlineData("  ", "1234")]
    [InlineData("王小明", null)]
    [InlineData("王小明", "")]
    [InlineData("王小明", " ")]
    public void RegisterRealName_WhenArgumentBlank_ThrowsArgumentException(string? realName, string? nationalIdLast4)
    {
        var member = Member.Register("user@example.com", "Alice", "hashed-password");

        var act = () => member.RegisterRealName(realName!, nationalIdLast4!);

        act.Should().Throw<ArgumentException>();
        member.HasRegisteredRealName.Should().BeFalse();
        member.NationalIdLast4.Should().BeNull();
    }

    // RNV-RETAIN-001：停用不清除實名，停用前售出的需實名票券才能查持票人與核銷。
    [Fact]
    public void Deactivate_WhenRealNameRegistered_KeepsRealName()
    {
        var member = Member.Register("user@example.com", "Alice", "hashed-password");
        member.RegisterRealName("王小明", "1234");

        member.Deactivate();
        member.RealName.Should().Be("王小明");
        member.NationalIdLast4.Should().Be("1234");

        member.Activate();
        member.RealName.Should().Be("王小明");
        member.NationalIdLast4.Should().Be("1234");
    }
}
