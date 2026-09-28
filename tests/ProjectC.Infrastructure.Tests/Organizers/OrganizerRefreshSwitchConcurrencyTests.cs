using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProjectC.Application.Authentication.Refresh;
using ProjectC.Application.Common;
using ProjectC.Application.Organizers.SwitchOrganizerContext;
using ProjectC.Domain.Authentication;
using ProjectC.Domain.Members;
using ProjectC.Domain.Organizers;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Security;
using ProjectC.Infrastructure.Tests.TestSupport;

namespace ProjectC.Infrastructure.Tests.Organizers;

/// <summary>
/// 驗證 organizer-management design.md「邊界情況：切換與換發併發競爭同一筆 Refresh Token」
/// （ORG-CONCURRENCY-001／002）：兩者對同一筆 RefreshToken 資料列的寫入依賴既有 Postgres xmin
/// 樂觀併發權杖，mock 無法驗證真實行為，MUST 用 Testcontainers Postgres。
///
/// 手法比照既有 TicketTypeConcurrencyTests 的說明：刻意利用 EF Core identity resolution——
/// 讓「輸家」的 DbContext 提前查出（並在 ChangeTracker 內卡住）這筆記錄的舊版本，「贏家」用另一個
/// 全新 DbContext 完整跑完並提交，之後才讓輸家的 Handler 執行；輸家的 Handler 內部查詢會因為
/// identity resolution 直接拿到同一個已追蹤的舊版本 entity（而非重新查詢資料庫取得新版本），
/// SaveChanges 時 WHERE xmin = 舊版本 因此不匹配任何列，觸發 DbUpdateConcurrencyException。
/// </summary>
[Collection(PostgresCollection.Name)]
public class OrganizerRefreshSwitchConcurrencyTests
{
    private readonly PostgresFixture _fixture;
    private static readonly SystemDateTimeProvider DateTimeProvider = new();

    public OrganizerRefreshSwitchConcurrencyTests(PostgresFixture fixture)
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

    private async Task<(Guid MemberId, Guid OrganizerId, Guid TokenId, string PlainTextToken)> SeedApprovedOrganizerWithActiveTokenAsync(ApplicationDbContext dbContext)
    {
        var member = Member.Register($"member-{Guid.NewGuid():N}@example.com", "Test Member", "hash");
        dbContext.Members.Add(member);

        var organizer = Organizer.Apply(Guid.NewGuid(), "Concurrency Test Organizer", member.Id, DateTimeProvider.UtcNow);
        organizer.Approve(member.Id, DateTimeProvider.UtcNow);
        dbContext.Organizers.Add(organizer);
        dbContext.OrganizerMembers.Add(new OrganizerMember(Guid.NewGuid(), organizer.Id, member.Id, OrganizerMemberRole.Owner));

        var tokenService = CreateTokenService();
        var plainTextToken = tokenService.GenerateOpaqueToken();
        var refreshToken = RefreshToken.Issue(member.Id, tokenService.HashOpaqueToken(plainTextToken), DateTimeProvider.UtcNow.AddDays(14));
        dbContext.RefreshTokens.Add(refreshToken);

        await dbContext.SaveChangesAsync();

        return (member.Id, organizer.Id, refreshToken.Id, plainTextToken);
    }

    [Fact]
    public async Task SwitchCommitsFirst_ThenRefreshOnStaleTrackedToken_RefreshFailsWith401AndSwitchWriteIsNotOverwritten()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var (memberId, organizerId, tokenId, plainTextToken) = await SeedApprovedOrganizerWithActiveTokenAsync(seedDbContext);

        // 「輸家」（換發）的 DbContext 提前查出並卡住這筆記錄的舊版本。
        await using var refreshDbContext = _fixture.CreateDbContext();
        _ = await refreshDbContext.RefreshTokens.SingleAsync(t => t.Id == tokenId);

        // 「贏家」（切換）用全新 DbContext 完整跑完並提交。
        await using var switchDbContext = _fixture.CreateDbContext();
        var switchHandler = new SwitchOrganizerContextHandler(
            switchDbContext, CreateTokenService(), DateTimeProvider, new SwitchOrganizerContextRequestValidator());
        var switchResult = await switchHandler.HandleAsync(
            memberId, organizerId, new SwitchOrganizerContextRequest(plainTextToken), CancellationToken.None);
        switchResult.IsSuccess.Should().BeTrue("切換先完成，MUST 正常成功");

        var refreshTokenCountBeforeRefresh = await _fixture.CreateDbContext().RefreshTokens.AsNoTracking()
            .CountAsync(t => t.MemberId == memberId);

        // 「輸家」用卡住舊版本的 DbContext 繼續執行換發：內部查詢因 identity resolution 拿到同一個
        // 已追蹤的舊版本 entity，SaveChanges 時因 xmin 不匹配而拋出 DbUpdateConcurrencyException。
        var refreshHandler = new RefreshTokenHandler(refreshDbContext, CreateTokenService(), DateTimeProvider, new AuthOptions());
        var refreshResult = await refreshHandler.HandleAsync(new RefreshTokenRequest(plainTextToken), CancellationToken.None);

        refreshResult.IsSuccess.Should().BeFalse("換發後完成，MUST 因併發衝突被拒絕");
        refreshResult.Error!.Type.Should().Be(ErrorType.Unauthorized);

        await using var readDbContext = _fixture.CreateDbContext();
        var reloadedToken = await readDbContext.RefreshTokens.AsNoTracking().SingleAsync(t => t.Id == tokenId);
        reloadedToken.OrganizerId.Should().Be(organizerId, "切換已寫入的 OrganizerId 更新結果不被輸家覆寫");

        var refreshTokenCountAfterRefresh = await readDbContext.RefreshTokens.AsNoTracking().CountAsync(t => t.MemberId == memberId);
        refreshTokenCountAfterRefresh.Should().Be(refreshTokenCountBeforeRefresh, "換發失敗，MUST NOT 建立新的 RefreshToken 資料列");
    }

    [Fact]
    public async Task RefreshCommitsFirst_ThenSwitchOnStaleTrackedToken_SwitchFailsWith401AndRefreshRotationIsNotOverwritten()
    {
        await using var seedDbContext = _fixture.CreateDbContext();
        var (memberId, organizerId, tokenId, plainTextToken) = await SeedApprovedOrganizerWithActiveTokenAsync(seedDbContext);

        // 「輸家」（切換）的 DbContext 提前查出並卡住這筆記錄的舊版本。
        await using var switchDbContext = _fixture.CreateDbContext();
        _ = await switchDbContext.RefreshTokens.SingleAsync(t => t.Id == tokenId);

        // 「贏家」（換發）用全新 DbContext 完整跑完並提交（MarkAsUsed + 新增輪替記錄）。
        await using var refreshDbContext = _fixture.CreateDbContext();
        var refreshHandler = new RefreshTokenHandler(refreshDbContext, CreateTokenService(), DateTimeProvider, new AuthOptions());
        var refreshResult = await refreshHandler.HandleAsync(new RefreshTokenRequest(plainTextToken), CancellationToken.None);
        refreshResult.IsSuccess.Should().BeTrue("換發先完成，MUST 正常成功");

        var newRefreshTokenId = await _fixture.CreateDbContext().RefreshTokens.AsNoTracking()
            .Where(t => t.PreviousTokenId == tokenId)
            .Select(t => t.Id)
            .SingleAsync();

        // 「輸家」用卡住舊版本的 DbContext 繼續執行切換：內部查詢因 identity resolution 拿到同一個
        // 已追蹤的舊版本 entity（Status 仍是 Active），SaveChanges 時因 xmin 不匹配而拋出
        // DbUpdateConcurrencyException（換發已把這筆記錄標記為 Used 並產生新記錄）。
        var switchHandler = new SwitchOrganizerContextHandler(
            switchDbContext, CreateTokenService(), DateTimeProvider, new SwitchOrganizerContextRequestValidator());
        var switchResult = await switchHandler.HandleAsync(
            memberId, organizerId, new SwitchOrganizerContextRequest(plainTextToken), CancellationToken.None);

        switchResult.IsSuccess.Should().BeFalse("切換後完成，MUST 因併發衝突被拒絕");
        switchResult.Error!.Type.Should().Be(ErrorType.Unauthorized);

        await using var readDbContext = _fixture.CreateDbContext();
        var reloadedOldToken = await readDbContext.RefreshTokens.AsNoTracking().SingleAsync(t => t.Id == tokenId);
        reloadedOldToken.OrganizerId.Should().BeNull("切換失敗，MUST NOT 更新任何 RefreshToken 記錄的 OrganizerId");

        var reloadedNewToken = await readDbContext.RefreshTokens.AsNoTracking().SingleAsync(t => t.Id == newRefreshTokenId);
        reloadedNewToken.OrganizerId.Should().BeNull("換發已產生的新 RefreshToken 記錄不受輸家影響");
    }
}
