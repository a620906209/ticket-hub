namespace ProjectC.Application.Common;

public sealed class Error
{
    public ErrorType Type { get; }
    public string Message { get; }
    public IReadOnlyDictionary<string, string[]>? FieldErrors { get; }

    private Error(ErrorType type, string message, IReadOnlyDictionary<string, string[]>? fieldErrors = null)
    {
        Type = type;
        Message = message;
        FieldErrors = fieldErrors;
    }

    public static Error Validation(string message) => new(ErrorType.Validation, message);

    // fieldErrors 的鍵為 FluentValidation 的原始屬性路徑（例如 Selections[0].Quantity），JSON 鍵格式化由 WebApi 負責。
    public static Error Validation(string message, IReadOnlyDictionary<string, string[]> fieldErrors) => new(ErrorType.Validation, message, fieldErrors);

    public static Error NotFound(string message) => new(ErrorType.NotFound, message);
    public static Error Conflict(string message) => new(ErrorType.Conflict, message);
    public static Error Unauthorized(string message) => new(ErrorType.Unauthorized, message);
    public static Error Forbidden(string message) => new(ErrorType.Forbidden, message);
    public static Error QueueAdmissionRequired(string message) => new(ErrorType.QueueAdmissionRequired, message);
    public static Error InvalidTicketSignature(string message) => new(ErrorType.InvalidTicketSignature, message);
    public static Error CaptchaInvalid(string message) => new(ErrorType.CaptchaInvalid, message);
    public static Error RealNameRequired(string message) => new(ErrorType.RealNameRequired, message);
    public static Error HolderVerificationRequired(string message) => new(ErrorType.HolderVerificationRequired, message);
    public static Error SalesNotOpen(string message) => new(ErrorType.SalesNotOpen, message);
    public static Error SalesClosed(string message) => new(ErrorType.SalesClosed, message);
}
