using Microsoft.EntityFrameworkCore;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;

namespace ProjectC.Application.Members.Activate;

public sealed class ActivateMemberHandler
{
    private readonly IApplicationDbContext _dbContext;

    public ActivateMemberHandler(IApplicationDbContext dbContext)
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

        member.Activate();
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
