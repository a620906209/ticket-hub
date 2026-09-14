namespace ProjectC.Application.Common.Interfaces;

public sealed record CaptchaChallenge(string Token, byte[] ImageBytes);
