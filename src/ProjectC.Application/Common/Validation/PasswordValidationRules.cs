using FluentValidation;

namespace ProjectC.Application.Common.Validation;

public static class PasswordValidationRules
{
    public static IRuleBuilderOptions<T, string> MustBeStrongPassword<T>(this IRuleBuilder<T, string> ruleBuilder)
    {
        return ruleBuilder
            .MinimumLength(8).WithMessage("密碼長度至少須為 8 碼。")
            .Matches("[A-Za-z]").WithMessage("密碼須包含英文字母。")
            .Matches("[0-9]").WithMessage("密碼須包含數字。");
    }
}
