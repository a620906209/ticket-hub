using FluentAssertions;
using ProjectC.Application.Authentication.PasswordReset;
using ProjectC.Application.Common;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Authentication;
using ProjectC.Domain.Members;

namespace ProjectC.Application.Tests.Authentication.PasswordReset;

public class ResetPasswordHandlerTests
{
    private readonly FakeApplicationDbContext _dbContext = new();
    private readonly FakePasswordHasher _passwordHasher = new();
    private readonly FakeTokenService _tokenService = new();
    private readonly FakeDateTimeProvider _dateTimeProvider = new();
    private readonly ResetPasswordHandler _handler;

    public ResetPasswordHandlerTests()
    {
        _handler = new ResetPasswordHandler(
            _dbContext,
            _passwordHasher,
            _tokenService,
            _dateTimeProvider,
            new ResetPasswordRequestValidator());
    }

    private (Member Member, string PlainTextToken) SeedValidResetToken()
    {
        var member = Member.Register("user@example.com", "Alice", _passwordHasher.HashPassword("OldPassword1"));
        _dbContext.MemberData.Add(member);

        var plainTextToken = _tokenService.GenerateOpaqueToken();
        var resetToken = PasswordResetToken.Issue(member.Id, _tokenService.HashOpaqueToken(plainTextToken), _dateTimeProvider.UtcNow.AddMinutes(15));
        _dbContext.PasswordResetTokenData.Add(resetToken);

        return (member, plainTextToken);
    }

    [Fact]
    public async Task HandleAsync_WithValidTokenAndStrongPassword_UpdatesPasswordAndRevokesRefreshTokens()
    {
        var (member, plainTextToken) = SeedValidResetToken();
        var activeRefreshToken = RefreshToken.Issue(member.Id, "hash-active", _dateTimeProvider.UtcNow.AddDays(14));
        _dbContext.RefreshTokenData.Add(activeRefreshToken);

        var result = await _handler.HandleAsync(new ResetPasswordRequest(plainTextToken, "NewPassword1"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _passwordHasher.VerifyPassword("NewPassword1", member.PasswordHash).Should().BeTrue();
        activeRefreshToken.Status.Should().Be(RefreshTokenStatus.Revoked);
    }

    [Fact]
    public async Task HandleAsync_WithExpiredToken_ReturnsValidationErrorAndKeepsOldPassword()
    {
        var member = Member.Register("user@example.com", "Alice", _passwordHasher.HashPassword("OldPassword1"));
        _dbContext.MemberData.Add(member);
        var plainTextToken = _tokenService.GenerateOpaqueToken();
        var expiredToken = PasswordResetToken.Issue(member.Id, _tokenService.HashOpaqueToken(plainTextToken), _dateTimeProvider.UtcNow.AddMinutes(-1));
        _dbContext.PasswordResetTokenData.Add(expiredToken);

        var result = await _handler.HandleAsync(new ResetPasswordRequest(plainTextToken, "NewPassword1"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
        _passwordHasher.VerifyPassword("OldPassword1", member.PasswordHash).Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WithAlreadyUsedToken_ReturnsValidationError()
    {
        var (_, plainTextToken) = SeedValidResetToken();
        _dbContext.PasswordResetTokenData.Single().MarkAsUsed();

        var result = await _handler.HandleAsync(new ResetPasswordRequest(plainTextToken, "NewPassword1"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Type.Should().Be(ErrorType.Validation);
    }
}
