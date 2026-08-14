using FluentValidation;
using ProjectC.Application.Common.Validation;

namespace ProjectC.Application.Authentication.PasswordReset;

public sealed class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator()
    {
        RuleFor(x => x.Token).NotEmpty();
        RuleFor(x => x.NewPassword).NotEmpty().MustBeStrongPassword();
    }
}
