using FluentAssertions;
using ProjectC.Application.Members.RegisterRealName;

namespace ProjectC.Application.Tests.Members.RegisterRealName;

/// <summary>實名登記後不可修改，格式錯誤的值一旦寫入就無法更正，所以格式只能在寫入前擋下（RNV-FORMAT-*）。</summary>
public class RegisterRealNameRequestValidatorTests
{
    private const string ValidRealName = "王小明";
    private const string ValidNationalIdLast4 = "1234";

    private readonly RegisterRealNameRequestValidator _validator = new();

    // RNV-FORMAT-001
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WhenRealNameBlank_Fails(string realName)
    {
        _validator.Validate(new RegisterRealNameRequest(realName, ValidNationalIdLast4)).IsValid.Should().BeFalse();
    }

    // RNV-FORMAT-002
    [Fact]
    public void Validate_WhenRealNameExceeds50Characters_Fails()
    {
        _validator.Validate(new RegisterRealNameRequest(new string('王', 51), ValidNationalIdLast4)).IsValid.Should().BeFalse();
    }

    // 長度以 trim 後計算：前後空白不得讓剛好 50 字的姓名被誤擋。
    [Fact]
    public void Validate_WhenTrimmedRealNameIsExactly50Characters_Passes()
    {
        _validator.Validate(new RegisterRealNameRequest($"  {new string('王', 50)}  ", ValidNationalIdLast4)).IsValid.Should().BeTrue();
    }

    // RNV-FORMAT-004：控制字元會讓核銷面板顯示錯亂或在日誌中偽造換行。
    [Theory]
    [InlineData("王\n小明")]
    [InlineData("王\t小明")]
    [InlineData("王\u0007小明")]
    [InlineData("王\u2028小明")]
    [InlineData("王\u2029小明")]
    public void Validate_WhenRealNameContainsControlCharacter_Fails(string realName)
    {
        _validator.Validate(new RegisterRealNameRequest(realName, ValidNationalIdLast4)).IsValid.Should().BeFalse();
    }

    // RNV-FORMAT-002：長度以 Unicode 字元計，擴充 B 區罕用字（UTF-16 佔兩個單位）50 字仍可登記、51 字被拒。
    [Theory]
    [InlineData(50, true)]
    [InlineData(51, false)]
    public void Validate_WhenRealNameUsesSupplementaryPlaneCharacters_CountsUnicodeCharacters(int characterCount, bool isValid)
    {
        var realName = string.Concat(Enumerable.Repeat("\U00020000", characterCount));

        _validator.Validate(new RegisterRealNameRequest(realName, ValidNationalIdLast4)).IsValid.Should().Be(isValid);
    }

    // RNV-FORMAT-007：看不見的格式字元會讓核銷面板顯示空白或順序顛倒的姓名，操作人員無從比對證件。
    [Theory]
    [InlineData("\u200B")]
    [InlineData("王\u200B小明")]
    [InlineData("王\u202E小明")]
    [InlineData("\uFEFF王小明")]
    [InlineData("王小明\U000E0001")]
    public void Validate_WhenRealNameContainsFormatCharacter_Fails(string realName)
    {
        _validator.Validate(new RegisterRealNameRequest(realName, ValidNationalIdLast4)).IsValid.Should().BeFalse();
    }

    // RNV-FORMAT-009：這些字元歸類為字母／符號（不是 Cc／Cf），但顯示為空白，同樣會讓核銷面板的姓名無從比對。
    [Theory]
    [InlineData("\u3164")]
    [InlineData("\u2800")]
    [InlineData("\u115F")]
    [InlineData("\u1160")]
    [InlineData("\uFFA0")]
    [InlineData("王\u3164小明")]
    public void Validate_WhenRealNameContainsBlankLookingLetter_Fails(string realName)
    {
        _validator.Validate(new RegisterRealNameRequest(realName, ValidNationalIdLast4)).IsValid.Should().BeFalse();
    }
    // RNV-FORMAT-010：黑名單列不完整，只由組合字元或符號組成的姓名同樣無從比對證件，至少要有一個字母類別的字元。
    [Theory]
    [InlineData("\u034F")]
    [InlineData("\uFE0F\uFE0F")]
    [InlineData("\u180B")]
    [InlineData("123")]
    [InlineData("．")]
    public void Validate_WhenRealNameHasNoLetter_Fails(string realName)
    {
        _validator.Validate(new RegisterRealNameRequest(realName, ValidNationalIdLast4)).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("王小明")]
    [InlineData("Mary-Jane O'Neil")]
    [InlineData("\U00020000")]
    public void Validate_WhenRealNameContainsLetter_Succeeds(string realName)
    {
        _validator.Validate(new RegisterRealNameRequest(realName, ValidNationalIdLast4)).IsValid.Should().BeTrue();
    }

    // RNV-FORMAT-005；"1234\n" 鎖定 .NET 正則 $ 允許尾端換行的陷阱。
    [Theory]
    [InlineData("123")]
    [InlineData("12345")]
    [InlineData("12a4")]
    [InlineData("１２３４")]
    [InlineData("12 4")]
    [InlineData("1234\n")]
    public void Validate_WhenNationalIdLast4NotFourAsciiDigits_Fails(string nationalIdLast4)
    {
        _validator.Validate(new RegisterRealNameRequest(ValidRealName, nationalIdLast4)).IsValid.Should().BeFalse();
    }

    // RNV-FORMAT-006：錯誤訊息會原樣進入 400 回應，不得回顯輸入值。
    [Fact]
    public void Validate_WhenNationalIdLast4Invalid_ErrorMessagesDoNotEchoInput()
    {
        var result = _validator.Validate(new RegisterRealNameRequest(ValidRealName, "98x7"));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().OnlyContain(e => !e.ErrorMessage.Contains("98x7"));
    }

    [Fact]
    public void Validate_WhenBothFieldsValid_Passes()
    {
        _validator.Validate(new RegisterRealNameRequest(ValidRealName, ValidNationalIdLast4)).IsValid.Should().BeTrue();
    }
}
