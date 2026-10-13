using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Authentication;

namespace ProjectC.Application.Authentication.Logout;

/// <summary>
/// 只憑 Refresh Token 明文撤銷（logout-revoke-by-refresh-token design.md 決策 1）：持有者本來就能用它換發，
/// 不要求 Access Token，Access Token 過期時也能撤銷。
/// </summary>
public sealed class LogoutHandler
{
    private const string TokenStateChangedMessage = "登出時 Token 狀態已變更";

    private readonly IApplicationDbContext _dbContext;
    private readonly ITokenService _tokenService;
    private readonly ILogger<LogoutHandler> _logger;

    public LogoutHandler(IApplicationDbContext dbContext, ITokenService tokenService, ILogger<LogoutHandler> logger)
    {
        _dbContext = dbContext;
        _tokenService = tokenService;
        _logger = logger;
    }

    public async Task<Result> HandleAsync(LogoutRequest request, CancellationToken cancellationToken)
    {
        var tokenHash = _tokenService.HashOpaqueToken(request.RefreshToken);
        var token = await _dbContext.RefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

        // 查無或非 Active 都回成功且不寫入：回應與成功相同，不揭露 Token 是否存在；
        // Used Token 不改成 Revoked，重複使用偵測留給換發端點（決策 2）。
        if (token is null || token.Status != RefreshTokenStatus.Active)
        {
            return Result.Success();
        }

        token.Revoke();
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // 換發或切換操作情境先一步寫入同一列：這次登出沒有撤銷到任何東西，回成功會是靜默失敗（決策 3）。
            // 只記識別碼，不記 Token 明文或雜湊。
            _logger.LogWarning(
                "Logout lost a concurrency race on refresh token {RefreshTokenId} of member {MemberId}; token was not revoked",
                token.Id, token.MemberId);
            return Result.Failure(Error.Conflict(TokenStateChangedMessage));
        }

        return Result.Success();
    }
}
