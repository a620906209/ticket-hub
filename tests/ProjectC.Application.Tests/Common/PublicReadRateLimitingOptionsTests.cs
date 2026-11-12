using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using ProjectC.Application.Common;

namespace ProjectC.Application.Tests.Common;

// api-rate-limiting spec PRL-005、PRL-006：預設值 120／60 讓部署不需新增設定即可運作；0 或負數會讓限流器
// 要嘛永遠拒絕、要嘛例外，必須被擋下（啟動時擋下的行為見 WebApi.Tests/Startup/RateLimitingOptionsFailFastTests）。
public class PublicReadRateLimitingOptionsTests
{
    [Fact]
    public void Defaults_ArePermitLimit120AndWindow60Seconds()
    {
        var options = new PublicReadRateLimitingOptions();

        options.PermitLimit.Should().Be(120);
        options.WindowSeconds.Should().Be(60);
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(-1, 60)]
    [InlineData(120, 0)]
    [InlineData(120, -1)]
    public void Validation_WithNonPositiveValues_Fails(int permitLimit, int windowSeconds)
    {
        var options = new PublicReadRateLimitingOptions { PermitLimit = permitLimit, WindowSeconds = windowSeconds };

        var isValid = Validator.TryValidateObject(options, new ValidationContext(options), [], validateAllProperties: true);

        isValid.Should().BeFalse();
    }

    [Fact]
    public void Validation_WithDefaults_Succeeds()
    {
        var options = new PublicReadRateLimitingOptions();

        var isValid = Validator.TryValidateObject(options, new ValidationContext(options), [], validateAllProperties: true);

        isValid.Should().BeTrue();
    }
}
