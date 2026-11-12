using FluentValidation;
using Microsoft.EntityFrameworkCore;
using ProjectC.Application.Common.Validation;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Authentication;

namespace ProjectC.Application.Authentication.Login;

public sealed class LoginHandler
{
    private const string InvalidCredentialsMessage = "Email 或密碼錯誤。";
    private const string InvalidCaptchaMessage = "驗證碼錯誤或已逾時，請重新取得驗證碼。";

    // 未知 Email 時仍需做一次完整的雜湊驗證，避免回應時間洩漏帳號是否存在。cost 11 須與
    // BCryptPasswordHasher 的預設 work factor 一致，否則耗時不同反而成為新的側通道。
    public const string DummyPasswordHash = "$2a$11$Zx1Tp2BEVSp4UTLIpVE3l.v7YHReF/4PnePEScNJcB17tNBJVjG4K";

    private readonly IApplicationDbContext _dbContext;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ITokenService _tokenService;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly AuthOptions _authOptions;
    private readonly IValidator<LoginRequest> _validator;
    private readonly ICaptchaService _captchaService;

    public LoginHandler(
        IApplicationDbContext dbContext,
        IPasswordHasher passwordHasher,
        ITokenService tokenService,
        IDateTimeProvider dateTimeProvider,
        AuthOptions authOptions,
        IValidator<LoginRequest> validator,
        ICaptchaService captchaService)
    {
        _dbContext = dbContext;
        _passwordHasher = passwordHasher;
        _tokenService = tokenService;
        _dateTimeProvider = dateTimeProvider;
        _authOptions = authOptions;
        _validator = validator;
        _captchaService = captchaService;
    }

    public async Task<Result<AuthTokensDto>> HandleAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return Result<AuthTokensDto>.Failure(validation.ToValidationError());
        }

        // 驗證碼檢查 MUST 在既有帳密比對之前執行，失敗時不繼續查詢會員資料（CAPTCHA-AUTH-001，
        // captcha-verification design.md 決策 5）。
        var captchaValid = await _captchaService.VerifyAsync(request.CaptchaToken, request.CaptchaAnswer, cancellationToken);
        if (!captchaValid)
        {
            return Result<AuthTokensDto>.Failure(Error.CaptchaInvalid(InvalidCaptchaMessage));
        }

        var member = await _dbContext.Members.FirstOrDefaultAsync(m => m.Email == request.Email, cancellationToken);
        var passwordHash = member?.PasswordHash ?? DummyPasswordHash;
        var isPasswordValid = _passwordHasher.VerifyPassword(request.Password, passwordHash);
        if (member is null || !isPasswordValid)
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
        var expiresAtUtc = _dateTimeProvider.UtcNow.AddDays(_authOptions.RefreshTokenExpirationDays);

        var refreshToken = RefreshToken.Issue(memberId, refreshTokenHash, expiresAtUtc, previousTokenId);
        _dbContext.RefreshTokens.Add(refreshToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return (plainTextRefreshToken, refreshToken);
    }
}
