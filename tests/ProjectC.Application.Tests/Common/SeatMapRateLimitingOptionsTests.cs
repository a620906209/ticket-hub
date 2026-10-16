using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using ProjectC.Application.Common;

namespace ProjectC.Application.Tests.Common;

// request-body-size-limits api-rate-limiting delta「建立座位圖限流設定值須為正數，缺漏時採用預設值」——比照
// CaptchaRateLimitingOptionsTests（SRL-004 的啟動失敗行為見 WebApi.Tests/Startup/RateLimitingOptionsFailFastTests）。
public class SeatMapRateLimitingOptionsTests
{
    // 區段缺漏時 Configuration Binder 不會改動執行個體，運作值就是屬性初始值；Application.Tests 不引用 Binder，直接驗證初始值。
    [Fact]
    public void Bind_WhenSectionMissing_UsesDefaults()
    {
        var options = new SeatMapRateLimitingOptions();

        options.PermitLimit.Should().Be(10);
        options.WindowSeconds.Should().Be(60);
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(-1, 60)]
    [InlineData(10, 0)]
    [InlineData(10, -1)]
    public void Validate_WhenValueIsZeroOrNegative_Fails(int permitLimit, int windowSeconds)
    {
        var options = new SeatMapRateLimitingOptions { PermitLimit = permitLimit, WindowSeconds = windowSeconds };

        var isValid = Validator.TryValidateObject(options, new ValidationContext(options), [], validateAllProperties: true);

        isValid.Should().BeFalse();
    }
}
