using ProjectC.Application.Common.Interfaces;

namespace ProjectC.Application.Captcha.GetCaptcha;

public sealed class GetCaptchaHandler
{
    private readonly ICaptchaService _captchaService;

    public GetCaptchaHandler(ICaptchaService captchaService)
    {
        _captchaService = captchaService;
    }

    public async Task<CaptchaResponseDto> HandleAsync(CancellationToken cancellationToken)
    {
        var challenge = await _captchaService.GenerateAsync(cancellationToken);
        return new CaptchaResponseDto(challenge.Token, Convert.ToBase64String(challenge.ImageBytes));
    }
}
