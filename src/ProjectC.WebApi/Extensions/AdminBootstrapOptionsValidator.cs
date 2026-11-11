using FluentValidation;
using Microsoft.Extensions.Options;
using ProjectC.Application.Common.Validation;
using ProjectC.Infrastructure.Persistence.Seeding;

namespace ProjectC.WebApi.Extensions;

// 放在 WebApi（比照 CaptchaOptionsValidator）；密碼與 Email 規則重用註冊用的 FluentValidation 規則。
// 失敗訊息只含設定項名稱與規則描述，MUST NOT 含設定值（尤其是密碼）。
public sealed class AdminBootstrapOptionsValidator : IValidateOptions<AdminBootstrapOptions>
{
    private const string EmailSettingName = $"{AdminBootstrapOptions.SectionName}:Email";
    private const string PasswordSettingName = $"{AdminBootstrapOptions.SectionName}:Password";

    private static readonly OptionsRulesValidator RulesValidator = new();

    public ValidateOptionsResult Validate(string? name, AdminBootstrapOptions options)
    {
        var isEmailBlank = string.IsNullOrWhiteSpace(options.Email);
        var isPasswordBlank = string.IsNullOrWhiteSpace(options.Password);
        if (isEmailBlank && isPasswordBlank)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();
        if (isEmailBlank)
        {
            failures.Add($"{EmailSettingName} 未設定；Email 與 Password 須同時設定，或同時留空。");
        }

        if (isPasswordBlank)
        {
            failures.Add($"{PasswordSettingName} 未設定；Email 與 Password 須同時設定，或同時留空。");
        }

        var validationResult = RulesValidator.Validate(options);
        failures.AddRange(validationResult.Errors.Select(error => $"{error.PropertyName}：{error.ErrorMessage}"));

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private sealed class OptionsRulesValidator : AbstractValidator<AdminBootstrapOptions>
    {
        public OptionsRulesValidator()
        {
            RuleFor(o => o.Email)
                .EmailAddress().WithMessage("Email 格式不合法（不得含前後空白）。")
                .Must(email => email == email.Trim())
                .WithMessage("Email 格式不合法（不得含前後空白）。")
                .OverridePropertyName(EmailSettingName)
                .When(o => !string.IsNullOrWhiteSpace(o.Email));

            RuleFor(o => o.Password)
                .MustBeStrongPassword()
                .OverridePropertyName(PasswordSettingName)
                .When(o => !string.IsNullOrWhiteSpace(o.Password));
        }
    }
}
