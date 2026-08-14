using FluentAssertions;
using ProjectC.Application.Members.Activate;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Members;

namespace ProjectC.Application.Tests.Members.Activate;

public class ActivateMemberHandlerTests
{
    private readonly FakeApplicationDbContext _dbContext = new();
    private readonly ActivateMemberHandler _handler;

    public ActivateMemberHandlerTests()
    {
        _handler = new ActivateMemberHandler(_dbContext);
    }

    [Fact]
    public async Task HandleAsync_WithDeactivatedMember_SetsIsActiveTrue()
    {
        var member = Member.Register("user@example.com", "Alice", "hashed:secret");
        member.Deactivate();
        _dbContext.MemberData.Add(member);

        var result = await _handler.HandleAsync(member.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        member.IsActive.Should().BeTrue();
    }
}
