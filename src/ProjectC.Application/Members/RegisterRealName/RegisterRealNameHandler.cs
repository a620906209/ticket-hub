using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Members;

namespace ProjectC.Application.Members.RegisterRealName;

public sealed class RegisterRealNameHandler
{
    private readonly IApplicationDbContext _dbContext;
    private readonly IMemberRealNameRepository _memberRealNameRepository;
    private readonly IValidator<RegisterRealNameRequest> _validator;
    private readonly ILogger<RegisterRealNameHandler> _logger;

    public RegisterRealNameHandler(
        IApplicationDbContext dbContext,
        IMemberRealNameRepository memberRealNameRepository,
        IValidator<RegisterRealNameRequest> validator,
        ILogger<RegisterRealNameHandler> logger)
    {
        _dbContext = dbContext;
        _memberRealNameRepository = memberRealNameRepository;
        _validator = validator;
        _logger = logger;
    }

    public async Task<Result<MemberProfileDto>> HandleAsync(Guid memberId, RegisterRealNameRequest request, CancellationToken cancellationToken)
    {
        var validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return Result<MemberProfileDto>.Failure(Error.Validation(string.Join(" ", validation.Errors.Select(e => e.ErrorMessage))));
        }

        // AsNoTracking：RegisterRealName 只改這份副本；若改到追蹤中的實體，同 scope 之後任何 SaveChanges
        // 都會無條件寫回實名，繞過 TryRegisterAsync 的 RealName == null 條件（design.md 決策 2）。
        var member = await _dbContext.Members.AsNoTracking().FirstOrDefaultAsync(m => m.Id == memberId, cancellationToken);
        if (member is null)
        {
            return Result<MemberProfileDto>.Failure(Error.NotFound($"Member '{memberId}' was not found."));
        }

        var realName = request.RealName.Trim();
        var alreadyRegistered = Error.Conflict($"Member '{memberId}' has already registered a real name.");

        if (!member.RegisterRealName(realName, request.NationalIdLast4))
        {
            _logger.LogInformation("Real name registration for member {MemberId} rejected: already registered.", memberId);
            return Result<MemberProfileDto>.Failure(alreadyRegistered);
        }

        // false 也可能是會員在讀取後被刪除，但目前系統沒有刪除會員的路徑，所以一律視為被並發請求搶先登記。
        if (!await _memberRealNameRepository.TryRegisterAsync(memberId, realName, request.NationalIdLast4, cancellationToken))
        {
            _logger.LogInformation("Real name registration for member {MemberId} rejected: concurrently registered.", memberId);
            return Result<MemberProfileDto>.Failure(alreadyRegistered);
        }

        _logger.LogInformation("Real name registered for member {MemberId}.", memberId);
        return Result<MemberProfileDto>.Success(MemberProfileDto.FromMember(member));
    }
}
