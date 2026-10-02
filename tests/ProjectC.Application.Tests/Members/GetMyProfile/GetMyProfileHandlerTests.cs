using FluentAssertions;
using ProjectC.Application.Common;
using ProjectC.Application.Members.GetMyProfile;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Members;

namespace ProjectC.Application.Tests.Members.GetMyProfile;

public class GetMyProfileHandlerTests
{
    private readonly FakeApplicationDbContext _dbContext = new();
    private readonly GetMyProfileHandler _handler;

    public GetMyProfileHandlerTests()
    {
        _handler = new GetMyProfileHandler(_dbContext);
    }

    [Fact]
    public async Task HandleAsync_WithExistingMember_ReturnsProfileWithoutPasswordHash()
    {
        var member = Member.Register("user@example.com", "Alice", "hashed:secret");
        _dbContext.MemberData.Add(member);

        var result = await _handler.HandleAsync(member.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Email.Should().Be("user@example.com");
        result.Value!.DisplayName.Should().Be("Alice");
    }

    [Fact]
    public async Task HandleAsync_WithUnknownMemberId_ReturnsNotFound()
    {
        var result = await _handler.HandleAsync(Guid.NewGuid(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.NotFound);
    }

    // MM-PROFILE-RN-001：前端依 HasRegisteredRealName 決定是否引導登記。
    [Fact]
    public async Task HandleAsync_WhenRealNameNotRegistered_ReturnsFalseAndNullRealNameFields()
    {
        var member = Member.Register("user@example.com", "Alice", "hashed:secret");
        _dbContext.MemberData.Add(member);

        var result = await _handler.HandleAsync(member.Id, CancellationToken.None);

        result.Value!.HasRegisteredRealName.Should().BeFalse();
        result.Value.RealName.Should().BeNull();
        result.Value.NationalIdLast4Masked.Should().BeNull();
    }

    // MM-PROFILE-RN-002：完整末四碼只在查詢持票人端點出現，個人資料一律遮蔽。
    [Fact]
    public async Task HandleAsync_WhenRealNameRegistered_ReturnsFullNameAndMaskedLast4()
    {
        var member = Member.Register("user@example.com", "Alice", "hashed:secret");
        member.RegisterRealName("王小明", "1234");
        _dbContext.MemberData.Add(member);

        var result = await _handler.HandleAsync(member.Id, CancellationToken.None);

        result.Value!.HasRegisteredRealName.Should().BeTrue();
        result.Value.RealName.Should().Be("王小明");
        result.Value.NationalIdLast4Masked.Should().Be("**34");
        new[] { result.Value.Email, result.Value.DisplayName, result.Value.Role, result.Value.RealName, result.Value.NationalIdLast4Masked }
            .Should().OnlyContain(value => value == null || !value.Contains("1234"));
    }
}
