using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectC.Application.Authentication.Logout;
using ProjectC.Application.Tests.TestSupport;
using ProjectC.Domain.Authentication;
using ProjectC.Domain.Members;

namespace ProjectC.Application.Tests.Authentication.Logout;

/// <summary>
/// logout-revoke-by-refresh-token：登出只憑 Refresh Token 撤銷（AUTH-LOGOUT-001～004、007）。
/// 併發衝突（AUTH-LOGOUT-005）需要真實 Postgres xmin，見 Infrastructure.Tests 的 LogoutConcurrencyTests。
/// </summary>
public class LogoutHandlerTests
{
    private readonly FakeApplicationDbContext _dbContext = new();
    private readonly FakeTokenService _tokenService = new();
    private readonly LogoutHandler _handler;

    public LogoutHandlerTests()
    {
        _handler = new LogoutHandler(_dbContext, _tokenService, NullLogger<LogoutHandler>.Instance);
    }

    private (RefreshToken Token, string PlainText) AddToken(Guid memberId, DateTime? expiresAt = null)
    {
        var plainTextToken = _tokenService.GenerateOpaqueToken();
        var token = RefreshToken.Issue(memberId, _tokenService.HashOpaqueToken(plainTextToken), expiresAt ?? DateTime.UtcNow.AddDays(14));
        _dbContext.RefreshTokenData.Add(token);
        return (token, plainTextToken);
    }

    private Guid AddMember()
    {
        var member = Member.Register($"user-{Guid.NewGuid():N}@example.com", "Alice", "hashed:secret");
        _dbContext.MemberData.Add(member);
        return member.Id;
    }

    // Handler 不再接收 memberId：Access Token 過期時前端沒有登入身分可帶，撤銷只能依 Token 本身（AUTH-LOGOUT-001／002）。
    [Fact]
    public async Task HandleAsync_WithActiveToken_RevokesTokenWithoutMemberContext()
    {
        var (token, plainText) = AddToken(AddMember());

        var result = await _handler.HandleAsync(new LogoutRequest(plainText), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        token.Status.Should().Be(RefreshTokenStatus.Revoked);
        _dbContext.SaveChangesCallCount.Should().Be(1);
    }

    // 查無 Token 時回應必須與成功相同，且不寫入，避免成為「Token 是否存在」的探測管道（AUTH-LOGOUT-003）。
    [Fact]
    public async Task HandleAsync_WithUnknownToken_SucceedsWithoutSaving()
    {
        AddToken(AddMember());

        var result = await _handler.HandleAsync(new LogoutRequest("does-not-exist"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _dbContext.SaveChangesCallCount.Should().Be(0);
    }

    // 登出是「放棄」Token 而非「使用」Token：Used Token 不得被改成 Revoked，也不得觸發換發端點那套全面撤銷（AUTH-LOGOUT-004）。
    [Fact]
    public async Task HandleAsync_WithUsedToken_SucceedsWithoutChangingAnyToken()
    {
        var memberId = AddMember();
        var (usedToken, usedPlainText) = AddToken(memberId);
        usedToken.MarkAsUsed();
        var (otherActiveToken, _) = AddToken(memberId);

        var result = await _handler.HandleAsync(new LogoutRequest(usedPlainText), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        usedToken.Status.Should().Be(RefreshTokenStatus.Used);
        otherActiveToken.Status.Should().Be(RefreshTokenStatus.Active);
        _dbContext.SaveChangesCallCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_WithRevokedToken_SucceedsWithoutSaving()
    {
        var memberId = AddMember();
        var (revokedToken, revokedPlainText) = AddToken(memberId);
        revokedToken.Revoke();
        var (otherActiveToken, _) = AddToken(memberId);

        var result = await _handler.HandleAsync(new LogoutRequest(revokedPlainText), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        revokedToken.Status.Should().Be(RefreshTokenStatus.Revoked);
        otherActiveToken.Status.Should().Be(RefreshTokenStatus.Active);
        _dbContext.SaveChangesCallCount.Should().Be(0);
    }

    // 撤銷條件只看 Status，不看有效期限；若實作改以「未過期」作為條件，這個測試會失敗（AUTH-LOGOUT-007）。
    [Fact]
    public async Task HandleAsync_WithActiveButExpiredToken_RevokesToken()
    {
        var (token, plainText) = AddToken(AddMember(), expiresAt: DateTime.UtcNow.AddDays(-1));

        var result = await _handler.HandleAsync(new LogoutRequest(plainText), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        token.Status.Should().Be(RefreshTokenStatus.Revoked);
        _dbContext.SaveChangesCallCount.Should().Be(1);
    }
}
