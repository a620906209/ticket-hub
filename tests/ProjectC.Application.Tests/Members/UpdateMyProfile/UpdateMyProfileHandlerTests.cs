using FluentAssertions;
using ProjectC.Application.Common;
using ProjectC.Application.Members.UpdateMyProfile;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Members;

namespace ProjectC.Application.Tests.Members.UpdateMyProfile;

public class UpdateMyProfileHandlerTests
{
    private readonly FakeApplicationDbContext _dbContext = new();
    private readonly UpdateMyProfileHandler _handler;

    public UpdateMyProfileHandlerTests()
    {
        _handler = new UpdateMyProfileHandler(_dbContext, new UpdateMyProfileRequestValidator());
    }

    [Fact]
    public async Task HandleAsync_WithValidDisplayName_UpdatesDisplayNameOnly()
    {
        var member = Member.Register("user@example.com", "Alice", "hashed:secret");
        _dbContext.MemberData.Add(member);

        var result = await _handler.HandleAsync(member.Id, new UpdateMyProfileRequest("Alice Chen"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        member.DisplayName.Should().Be("Alice Chen");
        member.Role.Should().Be(MemberRole.Member);
        member.IsActive.Should().BeTrue();
    }

    // 對應 spec 情境「嘗試修改角色或帳號狀態遭拒」：UpdateMyProfileRequest 只有 DisplayName 欄位，
    // Role/IsActive 在型別層級就無法被傳入，因此以「請求 DTO 沒有這些欄位」的結構本身作為防護，不需要額外的執行期拒絕邏輯。

    [Fact]
    public async Task HandleAsync_WithEmptyDisplayName_ReturnsValidationError()
    {
        var member = Member.Register("user@example.com", "Alice", "hashed:secret");
        _dbContext.MemberData.Add(member);

        var result = await _handler.HandleAsync(member.Id, new UpdateMyProfileRequest(""), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        member.DisplayName.Should().Be("Alice");
    }
}
