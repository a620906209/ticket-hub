using FluentValidation;
using Microsoft.EntityFrameworkCore;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Authentication;

namespace ProjectC.Application.Authentication.Login;

public sealed class LoginHandler
{
    private const string InvalidCredentialsMessage = "Email 或密碼錯誤。";

    private readonly IApplicationDbContext _dbContext;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ITokenService _tokenService;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly AuthOptions _authOptions;
    private readonly IValidator<LoginRequest> _validator;

    public LoginHandler(
        IApplicationDbContext dbContext,
        IPasswordHasher passwordHasher,
        ITokenService tokenService,
        IDateTimeProvider dateTimeProvider,
        AuthOptions authOptions,
        IValidator<LoginRequest> validator)
    {
        _dbContext = dbContext;
        _passwordHasher = passwordHasher;
        _tokenService = tokenService;
        _dateTimeProvider = dateTimeProvider;
        _authOptions = authOptions;
        _validator = validator;
    }

    public async Task<Result<AuthTokensDto>> HandleAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return Result<AuthTokensDto>.Failure(Error.Validation(string.Join(" ", validation.Errors.Select(e => e.ErrorMessage))));
        }

        var member = await _dbContext.Members.FirstOrDefaultAsync(m => m.Email == request.Email, cancellationToken);
        if (member is null || !_passwordHasher.VerifyPassword(request.Password, member.PasswordHash))
        {
            return Result<AuthTokensDto>.Failure(Error.Unauthorized(InvalidCredentialsMessage));
        }

        if (!member.IsActive)
        {
            return Result<AuthTokensDto>.Failure(Error.Forbidden("帳號已被停用。"));
        }

        var tokens = await IssueTokensAsync(member.Id, previousTokenId: null, cancellationToken);
        var accessToken = _tokenService.GenerateAccessToken(member);

        return Result<AuthTokensDto>.Success(new AuthTokensDto(accessToken, tokens.PlainTextRefreshToken));
    }

    private async Task<(string PlainTextRefreshToken, RefreshToken Entity)> IssueTokensAsync(
        Guid memberId,
        Guid? previousTokenId,
        CancellationToken cancellationToken)
    {
        var plainTextRefreshToken = _tokenService.GenerateOpaqueToken();
        var refreshTokenHash = _tokenService.HashOpaqueToken(plainTextRefreshToken);
        var expiresAt = _dateTimeProvider.UtcNow.AddDays(_authOptions.RefreshTokenExpirationDays);

        var refreshToken = RefreshToken.Issue(memberId, refreshTokenHash, expiresAt, previousTokenId);
        _dbContext.RefreshTokens.Add(refreshToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return (plainTextRefreshToken, refreshToken);
    }
}
