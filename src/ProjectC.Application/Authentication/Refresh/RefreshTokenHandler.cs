using Microsoft.EntityFrameworkCore;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Authentication;

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

        var plainTextRefreshToken = _tokenService.GenerateOpaqueToken();
        var newTokenHash = _tokenService.HashOpaqueToken(plainTextRefreshToken);
        var expiresAt = _dateTimeProvider.UtcNow.AddDays(_authOptions.RefreshTokenExpirationDays);
        var newToken = RefreshToken.Issue(member.Id, newTokenHash, expiresAt, existingToken.Id);
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

        var accessToken = _tokenService.GenerateAccessToken(member);
        return Result<AuthTokensDto>.Success(new AuthTokensDto(accessToken, plainTextRefreshToken));
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
