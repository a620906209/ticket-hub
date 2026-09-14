using FluentValidation;
using Microsoft.EntityFrameworkCore;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Members;

namespace ProjectC.Application.Members.Register;

public sealed class RegisterMemberHandler
{
    private const string InvalidCaptchaMessage = "驗證碼錯誤或已逾時，請重新取得驗證碼。";

    private readonly IApplicationDbContext _dbContext;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IValidator<RegisterMemberRequest> _validator;
    private readonly ICaptchaService _captchaService;

    public RegisterMemberHandler(
        IApplicationDbContext dbContext,
        IPasswordHasher passwordHasher,
        IValidator<RegisterMemberRequest> validator,
        ICaptchaService captchaService)
    {
        _dbContext = dbContext;
        _passwordHasher = passwordHasher;
        _validator = validator;
        _captchaService = captchaService;
    }

    public async Task<Result<Guid>> HandleAsync(RegisterMemberRequest request, CancellationToken cancellationToken)
    {
        var validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return Result<Guid>.Failure(Error.Validation(string.Join(" ", validation.Errors.Select(e => e.ErrorMessage))));
        }

        // 驗證碼檢查 MUST 在既有 Email 唯一性檢查之前執行，失敗時不繼續查詢會員資料（CAPTCHA-REG-001，
        // captcha-verification design.md 決策 5）。
        var captchaValid = await _captchaService.VerifyAsync(request.CaptchaToken, request.CaptchaAnswer, cancellationToken);
        if (!captchaValid)
        {
            return Result<Guid>.Failure(Error.CaptchaInvalid(InvalidCaptchaMessage));
        }

        var emailExists = await _dbContext.Members.AnyAsync(m => m.Email == request.Email, cancellationToken);
        if (emailExists)
        {
            return Result<Guid>.Failure(Error.Conflict("此 Email 已被註冊。"));
        }

        var passwordHash = _passwordHasher.HashPassword(request.Password);
        var member = Member.Register(request.Email, request.DisplayName, passwordHash);

        _dbContext.Members.Add(member);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Result<Guid>.Success(member.Id);
    }
}
