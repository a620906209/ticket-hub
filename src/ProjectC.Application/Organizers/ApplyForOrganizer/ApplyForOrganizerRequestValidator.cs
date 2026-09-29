using FluentValidation;

namespace ProjectC.Application.Organizers.ApplyForOrganizer;

public sealed class ApplyForOrganizerRequestValidator : AbstractValidator<ApplyForOrganizerRequest>
{
    public ApplyForOrganizerRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("主辦方名稱為必填。");
        RuleFor(x => x.Name)
            .Must(name => name.Trim().Length <= 100)
            .When(x => !string.IsNullOrWhiteSpace(x.Name))
            .WithMessage("主辦方名稱去除頭尾空白後長度不可超過 100 字元。");
    }
}
