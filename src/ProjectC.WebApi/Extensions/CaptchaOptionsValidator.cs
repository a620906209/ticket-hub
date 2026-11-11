using Microsoft.Extensions.Options;
using ProjectC.Application.Common;

namespace ProjectC.WebApi.Extensions;

// 放在 WebApi 而非 Application：判斷環境需要 IHostEnvironment，Application 不應依賴 Hosting 抽象
// （captcha-test-mode design.md 決策 3）。
public sealed class CaptchaOptionsValidator : IValidateOptions<CaptchaOptions>
{
    private readonly IHostEnvironment _environment;

    public CaptchaOptionsValidator(IHostEnvironment environment)
    {
        _environment = environment;
    }

    public ValidateOptionsResult Validate(string? name, CaptchaOptions options)
    {
        if (!Enum.IsDefined(options.VerificationMode))
        {
            return ValidateOptionsResult.Fail(
                $"Captcha:VerificationMode 的值 '{(int)options.VerificationMode}' 不是已定義的模式。");
        }

        if (_environment.IsProduction() && options.VerificationMode == CaptchaVerificationMode.AcceptAny)
        {
            return ValidateOptionsResult.Fail("Captcha:VerificationMode 為 AcceptAny 時不得用於 Production 環境。");
        }

        return ValidateOptionsResult.Success;
    }
}
