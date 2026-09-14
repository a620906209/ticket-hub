using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ProjectC.Application.Captcha.GetCaptcha;

namespace ProjectC.WebApi.Controllers;

// 匿名可存取：未登入使用者也需要在註冊頁看到驗證碼（captcha-verification design.md 決策 8）。
[ApiController]
[Route("api/captcha")]
public class CaptchaController : ControllerBase
{
    private readonly GetCaptchaHandler _getCaptchaHandler;

    public CaptchaController(GetCaptchaHandler getCaptchaHandler)
    {
        _getCaptchaHandler = getCaptchaHandler;
    }

    [EnableRateLimiting("captcha")]
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var response = await _getCaptchaHandler.HandleAsync(cancellationToken);
        return Ok(response);
    }
}
