using FluentValidation;
using Microsoft.EntityFrameworkCore;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Authentication;
using ProjectC.Domain.Organizers;

namespace ProjectC.Application.Organizers.SwitchOrganizerContext;

public sealed class SwitchOrganizerContextHandler
{
    private const string InvalidTokenMessage = "Refresh token 無效或已過期，請重新登入。";

    private readonly IApplicationDbContext _dbContext;
    private readonly ITokenService _tokenService;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly IValidator<SwitchOrganizerContextRequest> _validator;

    public SwitchOrganizerContextHandler(
        IApplicationDbContext dbContext,
        ITokenService tokenService,
        IDateTimeProvider dateTimeProvider,
        IValidator<SwitchOrganizerContextRequest> validator)
    {
        _dbContext = dbContext;
        _tokenService = tokenService;
        _dateTimeProvider = dateTimeProvider;
        _validator = validator;
    }

    public async Task<Result<SwitchOrganizerContextResultDto>> HandleAsync(
        Guid memberId,
        Guid organizerId,
        SwitchOrganizerContextRequest request,
        CancellationToken cancellationToken)
    {
        var validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return Result<SwitchOrganizerContextResultDto>.Failure(
                Error.Validation(string.Join(" ", validation.Errors.Select(e => e.ErrorMessage))));
        }

        // 依 TokenHash 精確定位「發起這次切換」的那一筆 Refresh Token 記錄（見 design.md 決策 1）；
        // 同一 Member 可能同時持有多筆有效 Refresh Token，只憑 MemberId 無法唯一定位。
        var tokenHash = _tokenService.HashOpaqueToken(request.RefreshToken);
        var refreshToken = await _dbContext.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);
        if (refreshToken is null
            || refreshToken.MemberId != memberId
            || refreshToken.Status != RefreshTokenStatus.Active
            || !refreshToken.IsActive(_dateTimeProvider.UtcNow))
        {
            return Result<SwitchOrganizerContextResultDto>.Failure(Error.Unauthorized(InvalidTokenMessage));
        }

        var organizer = await _dbContext.Organizers.FirstOrDefaultAsync(o => o.Id == organizerId, cancellationToken);
        if (organizer is null)
        {
            return Result<SwitchOrganizerContextResultDto>.Failure(Error.NotFound($"Organizer '{organizerId}' was not found."));
        }

        var isMember = await _dbContext.OrganizerMembers
            .AnyAsync(om => om.OrganizerId == organizerId && om.MemberId == memberId, cancellationToken);
        if (!isMember)
        {
            return Result<SwitchOrganizerContextResultDto>.Failure(Error.Forbidden("您不是這個主辦方的成員。"));
        }

        if (organizer.Status != OrganizerStatus.Approved)
        {
            return Result<SwitchOrganizerContextResultDto>.Failure(Error.Conflict($"Organizer '{organizerId}' 目前狀態為 {organizer.Status}，無法切換。"));
        }

        // 比照既有 RefreshTokenHandler 對「Refresh Token 背後的 Member」的防禦寫法：不用 FirstAsync
        // 直接假設一定存在，查無資料時明確回傳 401，而不是讓 InvalidOperationException 往外拋變成 500。
        var member = await _dbContext.Members.FirstOrDefaultAsync(m => m.Id == memberId, cancellationToken);
        if (member is null)
        {
            return Result<SwitchOrganizerContextResultDto>.Failure(Error.Unauthorized(InvalidTokenMessage));
        }

        refreshToken.UpdateOrganizerContext(organizerId);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // 與換發 Access Token 併發競爭同一筆 Refresh Token 記錄，一律回傳 401（見 design.md
            // 「邊界情況：切換與換發併發競爭同一筆 Refresh Token」／ORG-CONCURRENCY-002）。
            return Result<SwitchOrganizerContextResultDto>.Failure(Error.Unauthorized(InvalidTokenMessage));
        }

        var accessToken = _tokenService.GenerateAccessToken(member, organizerId);
        return Result<SwitchOrganizerContextResultDto>.Success(new SwitchOrganizerContextResultDto(accessToken));
    }
}
