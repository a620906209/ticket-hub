using FluentAssertions;
using ProjectC.Application.Members;

namespace ProjectC.Application.Tests.Members;

/// <summary>完整末四碼只能在查詢持票人端點出現；遮蔽格式錯誤（例如露出前兩碼）會讓個人資料頁洩漏完整識別資訊
/// （real-name-verification design.md 決策 5）。</summary>
public class RealNameMaskingTests
{
    // RNV-MASK-001：以前導 0 確認是保留末兩碼字元、不是做數值運算。
    [Fact]
    public void MaskNationalIdLast4_WhenGiven0912_ReturnsStarStar12()
    {
        RealNameMasking.MaskNationalIdLast4("0912").Should().Be("**12");
    }

    [Theory]
    [InlineData("1234", "**34")]
    [InlineData("0000", "**00")]
    [InlineData("9870", "**70")]
    public void MaskNationalIdLast4_WhenGivenFourDigits_KeepsOnlyLastTwo(string nationalIdLast4, string expected)
    {
        RealNameMasking.MaskNationalIdLast4(nationalIdLast4).Should().Be(expected);
    }
}
