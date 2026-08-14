using FluentValidation;
using ProjectC.Application.Common.Validation;

namespace ProjectC.Application.Members.Register;

public sealed class RegisterMemberRequestValidator : AbstractValidator<RegisterMemberRequest>
{
    public RegisterMemberRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).NotEmpty().MustBeStrongPassword();
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(100);
    }
}
