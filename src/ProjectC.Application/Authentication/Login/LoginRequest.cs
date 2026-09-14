namespace ProjectC.Application.Authentication.Login;

public sealed record LoginRequest(string Email, string Password, string CaptchaToken, string CaptchaAnswer);
