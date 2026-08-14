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
}
