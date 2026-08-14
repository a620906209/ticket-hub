namespace ProjectC.Application.Common;

public sealed class Error
{
    public ErrorType Type { get; }
    public string Message { get; }

    private Error(ErrorType type, string message)
    {
        Type = type;
        Message = message;
    }

    public static Error Validation(string message) => new(ErrorType.Validation, message);
    public static Error NotFound(string message) => new(ErrorType.NotFound, message);
    public static Error Conflict(string message) => new(ErrorType.Conflict, message);
    public static Error Unauthorized(string message) => new(ErrorType.Unauthorized, message);
    public static Error Forbidden(string message) => new(ErrorType.Forbidden, message);
}
