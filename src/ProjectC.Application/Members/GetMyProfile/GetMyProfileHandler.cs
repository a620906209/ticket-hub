using Microsoft.EntityFrameworkCore;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;

namespace ProjectC.Application.Members.GetMyProfile;

public sealed class GetMyProfileHandler
{
    private readonly IApplicationDbContext _dbContext;

    public GetMyProfileHandler(IApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Result<MemberProfileDto>> HandleAsync(Guid memberId, CancellationToken cancellationToken)
    {
        var member = await _dbContext.Members.FirstOrDefaultAsync(m => m.Id == memberId, cancellationToken);
        if (member is null)
        {
            return Result<MemberProfileDto>.Failure(Error.NotFound("找不到會員資料。"));
        }

        var dto = new MemberProfileDto(member.Id, member.Email, member.DisplayName, member.Role.ToString(), member.IsActive);
        return Result<MemberProfileDto>.Success(dto);
    }
}
