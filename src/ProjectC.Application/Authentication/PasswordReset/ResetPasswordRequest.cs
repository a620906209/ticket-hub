namespace ProjectC.Application.Authentication.PasswordReset;

public sealed record ResetPasswordRequest(string Token, string NewPassword);
