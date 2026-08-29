using FluentAssertions;
using ProjectC.Application.Common;

namespace ProjectC.Application.Tests.Common;

// api-rate-limiting spec LRL-007「登入端點限流設定值須為正數，缺漏時採用明確預設值」——
// LoginRateLimitingOptions 沒有 ValidateOnStart，.NET Options 綁定對缺漏的設定鍵保留 C# 層級預設值
// 不變，這裡直接驗證該預設值本身（見 login-rate-limiting design.md 決策 2；LRL-008 的擋下行為見
// WebApi.Tests/Startup/RateLimitingOptionsFailFastTests，比照既有 RateLimitingOptionsTests 手法）。
public class LoginRateLimitingOptionsTests
{
    [Fact]
    public void Defaults_ArePermitLimit5AndWindow60Seconds()
    {
        var options = new LoginRateLimitingOptions();

        options.PermitLimit.Should().Be(5);
        options.WindowSeconds.Should().Be(60);
    }
}
