using FluentAssertions;
using ProjectC.Application.Common;
using ProjectC.Application.Members.Register;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Members;

namespace ProjectC.Application.Tests.Members.Register;

public class RegisterMemberHandlerTests
{
    private readonly FakeApplicationDbContext _dbContext = new();
    private readonly RegisterMemberHandler _handler;

    public RegisterMemberHandlerTests()
    {
        _handler = new RegisterMemberHandler(_dbContext, new FakePasswordHasher(), new RegisterMemberRequestValidator());
    }

    [Fact]
    public async Task HandleAsync_WithUnusedEmailAndStrongPassword_CreatesMember()
    {
        var request = new RegisterMemberRequest("new@example.com", "Password123", "Alice");

        var result = await _handler.HandleAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _dbContext.MemberData.Should().ContainSingle(m => m.Email == "new@example.com" && m.IsActive);
    }

    [Fact]
    public async Task HandleAsync_WithEmailAlreadyRegistered_ReturnsConflict()
    {
        _dbContext.MemberData.Add(Member.Register("existing@example.com", "Bob", "hashed:Password123"));
        var request = new RegisterMemberRequest("existing@example.com", "Password123", "Alice");

        var result = await _handler.HandleAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Conflict);
        _dbContext.MemberData.Should().ContainSingle();
    }

    [Fact]
    public async Task HandleAsync_WithWeakPassword_ReturnsValidationError()
    {
        var request = new RegisterMemberRequest("new@example.com", "short", "Alice");

        var result = await _handler.HandleAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        _dbContext.MemberData.Should().BeEmpty();
    }
}
