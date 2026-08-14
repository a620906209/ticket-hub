using FluentValidation;
using Microsoft.EntityFrameworkCore;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Authentication;

namespace ProjectC.Application.Authentication.PasswordReset;

public sealed class ResetPasswordHandler
{
    private const string InvalidTokenMessage = "重設連結無效或已過期，請重新申請。";

    private readonly IApplicationDbContext _dbContext;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ITokenService _tokenService;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly IValidator<ResetPasswordRequest> _validator;

    public ResetPasswordHandler(
        IApplicationDbContext dbContext,
        IPasswordHasher passwordHasher,
        ITokenService tokenService,
        IDateTimeProvider dateTimeProvider,
        IValidator<ResetPasswordRequest> validator)
    {
        _dbContext = dbContext;
        _passwordHasher = passwordHasher;
        _tokenService = tokenService;
        _dateTimeProvider = dateTimeProvider;
        _validator = validator;
    }

    public async Task<Result> HandleAsync(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        var validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return Result.Failure(Error.Validation(string.Join(" ", validation.Errors.Select(e => e.ErrorMessage))));
        }

        var tokenHash = _tokenService.HashOpaqueToken(request.Token);
        var resetToken = await _dbContext.PasswordResetTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);
        if (resetToken is null || !resetToken.IsValid(_dateTimeProvider.UtcNow))
        {
            return Result.Failure(Error.Validation(InvalidTokenMessage));
        }

        var member = await _dbContext.Members.FirstOrDefaultAsync(m => m.Id == resetToken.MemberId, cancellationToken);
        if (member is null)
        {
            return Result.Failure(Error.Validation(InvalidTokenMessage));
        }

        member.ChangePasswordHash(_passwordHasher.HashPassword(request.NewPassword));
        resetToken.MarkAsUsed();

        var activeTokens = await _dbContext.RefreshTokens
            .Where(t => t.MemberId == member.Id && t.Status != RefreshTokenStatus.Revoked)
            .ToListAsync(cancellationToken);

        foreach (var token in activeTokens)
        {
            token.Revoke();
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
