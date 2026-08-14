using Microsoft.EntityFrameworkCore;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;

namespace ProjectC.Application.Authentication.Logout;

public sealed class LogoutHandler
{
    private readonly IApplicationDbContext _dbContext;
    private readonly ITokenService _tokenService;

    public LogoutHandler(IApplicationDbContext dbContext, ITokenService tokenService)
    {
        _dbContext = dbContext;
        _tokenService = tokenService;
    }

    public async Task<Result> HandleAsync(Guid memberId, LogoutRequest request, CancellationToken cancellationToken)
    {
        var tokenHash = _tokenService.HashOpaqueToken(request.RefreshToken);
        var token = await _dbContext.RefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash && t.MemberId == memberId, cancellationToken);

        if (token is not null)
        {
            token.Revoke();
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return Result.Success();
    }
}
