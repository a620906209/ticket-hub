using FluentAssertions;
using ProjectC.Application.Common;

namespace ProjectC.Application.Tests.Common;

// api-rate-limiting spec RL-008「限流設定值須為正數，缺漏時採用明確預設值」——RateLimitingOptions 沒有
// ValidateOnStart，.NET Options 綁定對缺漏的設定鍵保留 C# 層級預設值不變，這裡直接驗證該預設值本身
// （見 rate-limiting-queue design.md 決策 1；RL-009 的擋下行為見 WebApi.Tests/Startup/RateLimitingOptionsFailFastTests）。
public class RateLimitingOptionsTests
{
    [Fact]
    public void Defaults_ArePermitLimit20AndWindow60Seconds()
    {
        var options = new RateLimitingOptions();

        options.PermitLimit.Should().Be(20);
        options.WindowSeconds.Should().Be(60);
    }
}
