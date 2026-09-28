using Microsoft.EntityFrameworkCore;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Authentication;
using ProjectC.Domain.Organizers;

namespace ProjectC.Application.Authentication.Refresh;

public sealed class RefreshTokenHandler
{
    private const string InvalidTokenMessage = "Refresh token 無效或已過期，請重新登入。";

    private readonly IApplicationDbContext _dbContext;
    private readonly ITokenService _tokenService;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly AuthOptions _authOptions;

    public RefreshTokenHandler(
        IApplicationDbContext dbContext,
        ITokenService tokenService,
        IDateTimeProvider dateTimeProvider,
        AuthOptions authOptions)
    {
        _dbContext = dbContext;
        _tokenService = tokenService;
        _dateTimeProvider = dateTimeProvider;
        _authOptions = authOptions;
    }

    public async Task<Result<AuthTokensDto>> HandleAsync(RefreshTokenRequest request, CancellationToken cancellationToken)
    {
        var tokenHash = _tokenService.HashOpaqueToken(request.RefreshToken);
        var existingToken = await _dbContext.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);
        if (existingToken is null)
        {
            return Result<AuthTokensDto>.Failure(Error.Unauthorized(InvalidTokenMessage));
        }

        var member = await _dbContext.Members.FirstOrDefaultAsync(m => m.Id == existingToken.MemberId, cancellationToken);
        if (member is null || !member.IsActive)
        {
            return Result<AuthTokensDto>.Failure(Error.Unauthorized(InvalidTokenMessage));
        }

        if (existingToken.Status != RefreshTokenStatus.Active)
        {
            // Token 已被使用過或已撤銷卻仍被提交，視為疑似遭竊，撤銷該會員所有 Token。
            await RevokeAllTokensAsync(member.Id, cancellationToken);
            return Result<AuthTokensDto>.Failure(Error.Unauthorized(InvalidTokenMessage));
        }

        if (!existingToken.IsActive(_dateTimeProvider.UtcNow))
        {
            return Result<AuthTokensDto>.Failure(Error.Unauthorized(InvalidTokenMessage));
        }

        existingToken.MarkAsUsed();

        // 若目前這筆 Refresh Token 記錄帶有 OrganizerId（由切換操作情境寫入，見 organizer-management
        // design.md 決策 2），換發時 MUST 重新驗證其仍然有效：呼叫者仍是該 Organizer 的成員、且該
        // Organizer 狀態仍為 Approved。驗證未通過（已被停權，或成員資格不存在）則新 Token 一律不帶
        // OrganizerId／claim（fail-closed，見 ORG-REFRESH-003／004）。
        var organizerIdForNewToken = await ResolveOrganizerIdForRefreshAsync(existingToken.OrganizerId, member.Id, cancellationToken);

        var plainTextRefreshToken = _tokenService.GenerateOpaqueToken();
        var newTokenHash = _tokenService.HashOpaqueToken(plainTextRefreshToken);
        var expiresAt = _dateTimeProvider.UtcNow.AddDays(_authOptions.RefreshTokenExpirationDays);
        var newToken = RefreshToken.Issue(member.Id, newTokenHash, expiresAt, existingToken.Id, organizerIdForNewToken);
        _dbContext.RefreshTokens.Add(newToken);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // 另一個併發請求已先一步消費了這個 Token，本次請求視為失敗，不觸發全面撤銷（非攻擊行為）。
            return Result<AuthTokensDto>.Failure(Error.Unauthorized(InvalidTokenMessage));
        }

        var accessToken = _tokenService.GenerateAccessToken(member, organizerIdForNewToken);
        return Result<AuthTokensDto>.Success(new AuthTokensDto(accessToken, plainTextRefreshToken));
    }

    private async Task<Guid?> ResolveOrganizerIdForRefreshAsync(Guid? currentOrganizerId, Guid memberId, CancellationToken cancellationToken)
    {
        if (currentOrganizerId is not { } organizerId)
        {
            return null;
        }

        var isStillMember = await _dbContext.OrganizerMembers
            .AnyAsync(om => om.OrganizerId == organizerId && om.MemberId == memberId, cancellationToken);
        if (!isStillMember)
        {
            return null;
        }

        var organizer = await _dbContext.Organizers.FirstOrDefaultAsync(o => o.Id == organizerId, cancellationToken);
        return organizer is { Status: OrganizerStatus.Approved } ? organizerId : null;
    }

    private async Task RevokeAllTokensAsync(Guid memberId, CancellationToken cancellationToken)
    {
        var tokens = await _dbContext.RefreshTokens
            .Where(t => t.MemberId == memberId && t.Status != RefreshTokenStatus.Revoked)
            .ToListAsync(cancellationToken);

        foreach (var token in tokens)
        {
            token.Revoke();
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
