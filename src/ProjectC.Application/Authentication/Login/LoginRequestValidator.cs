using FluentValidation;
using ProjectC.Application.Common.Validation;

namespace ProjectC.Application.Authentication.Login;

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).NotEmpty();
        this.ApplyCaptchaValidation(x => x.CaptchaToken, x => x.CaptchaAnswer);
    }
}
