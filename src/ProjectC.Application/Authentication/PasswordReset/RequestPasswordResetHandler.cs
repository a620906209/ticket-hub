using Microsoft.EntityFrameworkCore;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Authentication;

namespace ProjectC.Application.Authentication.PasswordReset;

public sealed class RequestPasswordResetHandler
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ITokenService _tokenService;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly AuthOptions _authOptions;

    public RequestPasswordResetHandler(
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

    /// <summary>
    /// 成功時回傳的明文 Token 僅供未來 Email 寄送整合使用；WebApi 層不得將此值回傳給呼叫端（避免帳號枚舉與 Token 外洩）。
    /// Email 不存在時仍回傳成功（Value 為 null），避免透過回應差異枚舉帳號。
    /// </summary>
    public async Task<Result<string?>> HandleAsync(RequestPasswordResetRequest request, CancellationToken cancellationToken)
    {
        var member = await _dbContext.Members.FirstOrDefaultAsync(m => m.Email == request.Email, cancellationToken);
        if (member is null)
        {
            return Result<string?>.Success(null);
        }

        var plainTextToken = _tokenService.GenerateOpaqueToken();
        var tokenHash = _tokenService.HashOpaqueToken(plainTextToken);
        var expiresAt = _dateTimeProvider.UtcNow.AddMinutes(_authOptions.PasswordResetTokenExpirationMinutes);

        var resetToken = PasswordResetToken.Issue(member.Id, tokenHash, expiresAt);
        _dbContext.PasswordResetTokens.Add(resetToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Result<string?>.Success(plainTextToken);
    }
}
