using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProjectC.Application.Authentication.Logout;
using ProjectC.Application.Authentication.Refresh;
using ProjectC.Application.Common;
using ProjectC.Application.Organizers.SwitchOrganizerContext;
using ProjectC.Domain.Authentication;
using ProjectC.Domain.Members;
using ProjectC.Domain.Organizers;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Security;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests.Authentication;

/// <summary>
/// 驗證 logout-revoke-by-refresh-token AUTH-LOGOUT-005：登出與換發／切換操作情境併發寫入同一筆 RefreshToken 時，
/// 登出 MUST 回 Conflict（而非未捕捉例外造成 500），且 Warning log 不得帶出 Token 明文或雜湊。
/// 依賴 Postgres xmin 樂觀併發權杖，mock 無法驗證，MUST 用 Testcontainers Postgres。
///
/// 手法比照 OrganizerRefreshSwitchConcurrencyTests：「輸家」（登出）的 DbContext 先查出並追蹤舊版本，
/// 「贏家」用另一個 DbContext 完整提交，之後輸家的 Handler 內部查詢因 identity resolution 拿到同一個舊版本 entity，
/// SaveChanges 時 WHERE xmin = 舊版本 不匹配，觸發 DbUpdateConcurrencyException。
/// </summary>
[Collection(PostgresCollection.Name)]
public class LogoutConcurrencyTests
{
    private readonly PostgresFixture _fixture;
    private static readonly SystemDateTimeProvider DateTimeProvider = new();

    public LogoutConcurrencyTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private static JwtTokenService CreateTokenService() => new(Options.Create(new JwtOptions
    {
        Issuer = "concurrency-test",
        Audience = "concurrency-test",
        SigningKey = "concurrency-test-jwt-signing-key-not-for-prod-32+",
        AccessTokenExpirationMinutes = 15,
    }));

    private static async Task<(Guid MemberId, Guid OrganizerId, Guid TokenId, string PlainTextToken, string TokenHash)> SeedApprovedOrganizerWithActiveTokenAsync(ApplicationDbContext dbContext)
    {
        var member = Member.Register($"member-{Guid.NewGuid():N}@example.com", "Test Member", "hash");
        dbContext.Members.Add(member);

        var organizer = Organizer.Apply(Guid.NewGuid(), "Logout Concurrency Organizer", member.Id, DateTimeProvider.UtcNow);
        organizer.Approve(member.Id, DateTimeProvider.UtcNow);
        dbContext.Organizers.Add(organizer);
        dbContext.OrganizerMembers.Add(new OrganizerMember(Guid.NewGuid(), organizer.Id, member.Id, OrganizerMemberRole.Owner));

        var tokenService = CreateTokenService();
        var plainTextToken = tokenService.GenerateOpaqueToken();
        var tokenHash = tokenService.HashOpaqueToken(plainTextToken);
        var refreshToken = RefreshToken.Issue(member.Id, tokenHash, DateTimeProvider.UtcNow.AddDays(14));
        dbContext.RefreshTokens.Add(refreshToken);

        await dbContext.SaveChangesAsync();

        return (member.Id, organizer.Id, refreshToken.Id, plainTextToken, tokenHash);
    }

    private static void AssertSingleWarningWithoutToken(ListLogger<LogoutHandler> logger, string plainTextToken, string tokenHash)
    {
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning);
        foreach (var entry in logger.Entries)
        {
            var loggedTexts = entry.State.Values.Select(v => v?.ToString() ?? string.Empty).Append(entry.Message).ToList();
            loggedTexts.Should().NotContain(text => text.Contains(plainTextToken), "log MUST NOT 帶出 Refresh Token 明文");
            loggedTexts.Should().NotContain(text => text.Contains(tokenHash), "log MUST NOT 帶出 Refresh Token 雜湊");
        }
    }

    [Fact]
    public async Task HandleAsync_WhenRefreshCommitsFirst_ReturnsConflict()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var (memberId, _, tokenId, plainTextToken, tokenHash) = await SeedApprovedOrganizerWithActiveTokenAsync(seedDbContext);

        await using var logoutDbContext = _fixture.CreateDbContext();
        _ = await logoutDbContext.RefreshTokens.SingleAsync(t => t.Id == tokenId);

        await using var refreshDbContext = _fixture.CreateDbContext();
        var refreshHandler = new RefreshTokenHandler(refreshDbContext, CreateTokenService(), DateTimeProvider, new AuthOptions());
        var refreshResult = await refreshHandler.HandleAsync(new RefreshTokenRequest(plainTextToken), CancellationToken.None);
        refreshResult.IsSuccess.Should().BeTrue("換發先完成，MUST 正常成功");

        var logger = new ListLogger<LogoutHandler>();
        var logoutHandler = new LogoutHandler(logoutDbContext, CreateTokenService(), logger);
        var logoutResult = await logoutHandler.HandleAsync(new LogoutRequest(plainTextToken), CancellationToken.None);

        logoutResult.IsSuccess.Should().BeFalse("登出後完成，MUST 因併發衝突失敗而非靜默成功");
        logoutResult.Error!.Type.Should().Be(ErrorType.Conflict);
        AssertSingleWarningWithoutToken(logger, plainTextToken, tokenHash);

        // 鎖定 design.md 決策 3 的殘餘風險：舊 Token 已被輪替、後繼 Token 仍為 Active，登出沒有撤銷到它。
        await using var readDbContext = _fixture.CreateDbContext();
        var oldToken = await readDbContext.RefreshTokens.AsNoTracking().SingleAsync(t => t.Id == tokenId);
        oldToken.Status.Should().Be(RefreshTokenStatus.Used);
        var successor = await readDbContext.RefreshTokens.AsNoTracking().SingleAsync(t => t.PreviousTokenId == tokenId);
        successor.Status.Should().Be(RefreshTokenStatus.Active);
        successor.MemberId.Should().Be(memberId);
    }

    [Fact]
    public async Task HandleAsync_WhenSwitchContextCommitsFirst_ReturnsConflict()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var (memberId, organizerId, tokenId, plainTextToken, tokenHash) = await SeedApprovedOrganizerWithActiveTokenAsync(seedDbContext);

        await using var logoutDbContext = _fixture.CreateDbContext();
        _ = await logoutDbContext.RefreshTokens.SingleAsync(t => t.Id == tokenId);

        await using var switchDbContext = _fixture.CreateDbContext();
        var switchHandler = new SwitchOrganizerContextHandler(
            switchDbContext, CreateTokenService(), DateTimeProvider, new SwitchOrganizerContextRequestValidator());
        var switchResult = await switchHandler.HandleAsync(
            memberId, organizerId, new SwitchOrganizerContextRequest(plainTextToken), CancellationToken.None);
        switchResult.IsSuccess.Should().BeTrue("切換先完成，MUST 正常成功");

        var logger = new ListLogger<LogoutHandler>();
        var logoutHandler = new LogoutHandler(logoutDbContext, CreateTokenService(), logger);
        var logoutResult = await logoutHandler.HandleAsync(new LogoutRequest(plainTextToken), CancellationToken.None);

        logoutResult.IsSuccess.Should().BeFalse();
        logoutResult.Error!.Type.Should().Be(ErrorType.Conflict);
        AssertSingleWarningWithoutToken(logger, plainTextToken, tokenHash);

        await using var readDbContext = _fixture.CreateDbContext();
        var reloadedToken = await readDbContext.RefreshTokens.AsNoTracking().SingleAsync(t => t.Id == tokenId);
        reloadedToken.Status.Should().Be(RefreshTokenStatus.Active, "登出失敗，輸家的 Revoked 寫入 MUST NOT 生效");
        reloadedToken.OrganizerId.Should().Be(organizerId, "切換已寫入的 OrganizerId 不被輸家覆寫");
    }
}
