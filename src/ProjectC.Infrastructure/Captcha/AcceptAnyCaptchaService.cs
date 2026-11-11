using ProjectC.Application.Common.Interfaces;

namespace ProjectC.Infrastructure.Captcha;

// 僅供非 Production 的自動化測試使用（captcha-test-mode）：核對恆通過，產生端點行為不變。
public sealed class AcceptAnyCaptchaService : ICaptchaService
{
    private readonly RedisCaptchaService _innerService;

    public AcceptAnyCaptchaService(RedisCaptchaService innerService)
    {
        _innerService = innerService;
    }

    public Task<CaptchaChallenge> GenerateAsync(CancellationToken cancellationToken)
        => _innerService.GenerateAsync(cancellationToken);

    public Task<bool> VerifyAsync(string token, string answer, CancellationToken cancellationToken)
        => Task.FromResult(true);
}
