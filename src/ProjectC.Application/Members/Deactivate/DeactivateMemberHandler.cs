using Microsoft.EntityFrameworkCore;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Authentication;

namespace ProjectC.Application.Members.Deactivate;

public sealed class DeactivateMemberHandler
{
    private readonly IApplicationDbContext _dbContext;

    public DeactivateMemberHandler(IApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Result> HandleAsync(Guid memberId, CancellationToken cancellationToken)
    {
        var member = await _dbContext.Members.FirstOrDefaultAsync(m => m.Id == memberId, cancellationToken);
        if (member is null)
        {
            return Result.Failure(Error.NotFound("找不到會員資料。"));
        }

        member.Deactivate();

        var tokensToRevoke = await _dbContext.RefreshTokens
            .Where(t => t.MemberId == memberId && t.Status != RefreshTokenStatus.Revoked)
            .ToListAsync(cancellationToken);

        foreach (var token in tokensToRevoke)
        {
            token.Revoke();
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
