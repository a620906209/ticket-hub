using FluentValidation;

namespace ProjectC.Application.Events.SetEventQueueMode;

public sealed class SetEventQueueModeRequestValidator : AbstractValidator<SetEventQueueModeRequest>
{
    public SetEventQueueModeRequestValidator()
    {
        RuleFor(x => x.Enabled).NotNull();
    }
}
