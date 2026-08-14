using FluentValidation;
using Microsoft.EntityFrameworkCore;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;

namespace ProjectC.Application.Members.UpdateMyProfile;

public sealed class UpdateMyProfileHandler
{
    private readonly IApplicationDbContext _dbContext;
    private readonly IValidator<UpdateMyProfileRequest> _validator;

    public UpdateMyProfileHandler(IApplicationDbContext dbContext, IValidator<UpdateMyProfileRequest> validator)
    {
        _dbContext = dbContext;
        _validator = validator;
    }

    public async Task<Result<MemberProfileDto>> HandleAsync(Guid memberId, UpdateMyProfileRequest request, CancellationToken cancellationToken)
    {
        var validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return Result<MemberProfileDto>.Failure(Error.Validation(string.Join(" ", validation.Errors.Select(e => e.ErrorMessage))));
        }

        var member = await _dbContext.Members.FirstOrDefaultAsync(m => m.Id == memberId, cancellationToken);
        if (member is null)
        {
            return Result<MemberProfileDto>.Failure(Error.NotFound("找不到會員資料。"));
        }

        member.ChangeDisplayName(request.DisplayName);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var dto = new MemberProfileDto(member.Id, member.Email, member.DisplayName, member.Role.ToString(), member.IsActive);
        return Result<MemberProfileDto>.Success(dto);
    }
}
