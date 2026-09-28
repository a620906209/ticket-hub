using FluentValidation;

namespace ProjectC.Application.Organizers.SwitchOrganizerContext;

public sealed class SwitchOrganizerContextRequestValidator : AbstractValidator<SwitchOrganizerContextRequest>
{
    public SwitchOrganizerContextRequestValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty().WithMessage("Refresh token 為必填。");
    }
}
