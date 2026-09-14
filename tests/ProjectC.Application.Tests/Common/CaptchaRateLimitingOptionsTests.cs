using FluentAssertions;
using ProjectC.Application.Common;

namespace ProjectC.Application.Tests.Common;

// captcha-verification spec「驗證碼限流設定值須為正數，缺漏時採用明確預設值」——比照既有
// LoginRateLimitingOptionsTests 手法（design.md 決策 6；CAPTCHA-RATE-003 的擋下行為見
// WebApi.Tests/Startup/RateLimitingOptionsFailFastTests）。
public class CaptchaRateLimitingOptionsTests
{
    [Fact]
    public void Defaults_ArePermitLimit10AndWindow60Seconds()
    {
        var options = new CaptchaRateLimitingOptions();

        options.PermitLimit.Should().Be(10);
        options.WindowSeconds.Should().Be(60);
    }
}
