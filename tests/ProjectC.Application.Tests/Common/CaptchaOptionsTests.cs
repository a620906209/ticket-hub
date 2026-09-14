using FluentAssertions;
using ProjectC.Application.Common;

namespace ProjectC.Application.Tests.Common;

// captcha-verification spec「驗證碼時效設定值須為正數，缺漏時採用明確預設值」——CaptchaOptions
// 沒有 ValidateOnStart，.NET Options 綁定對缺漏的設定鍵保留 C# 層級預設值不變，這裡直接驗證該
// 預設值本身（design.md 決策 14；CAPTCHA-STORE-004 的擋下行為見 WebApi.Tests/Startup/
// RateLimitingOptionsFailFastTests，比照既有 LoginRateLimitingOptionsTests 手法）。
public class CaptchaOptionsTests
{
    [Fact]
    public void Defaults_TtlSecondsIs120()
    {
        var options = new CaptchaOptions();

        options.TtlSeconds.Should().Be(120);
    }
}
