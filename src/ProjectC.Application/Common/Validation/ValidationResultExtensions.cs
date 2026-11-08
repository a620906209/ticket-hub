using FluentValidation.Results;

namespace ProjectC.Application.Common.Validation;

public static class ValidationResultExtensions
{
    // Message 必須維持驗證器回報順序的串接：欄位分組會讓交錯的訊息重新排序，兩者無法同時「逐字相同」，
    // 所以 detail 與 errors 各自獨立產生（validation-error-fields design.md 決策 7）。
    public static Error ToValidationError(this ValidationResult validationResult)
    {
        var message = string.Join(" ", validationResult.Errors.Select(failure => failure.ErrorMessage));
        var fieldErrors = validationResult.Errors
            .GroupBy(failure => failure.PropertyName)
            .ToDictionary(group => group.Key, group => group.Select(failure => failure.ErrorMessage).ToArray());

        return Error.Validation(message, fieldErrors);
    }
}
