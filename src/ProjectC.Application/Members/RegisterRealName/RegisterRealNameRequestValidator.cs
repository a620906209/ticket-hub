using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FluentValidation;

namespace ProjectC.Application.Members.RegisterRealName;

/// <summary>錯誤訊息一律自訂且不使用 {PropertyValue}：400 回應不得回顯姓名或末四碼（RNV-FORMAT-006）。</summary>
public sealed class RegisterRealNameRequestValidator : AbstractValidator<RegisterRealNameRequest>
{
    public const int RealNameMaxLength = 50;

    public RegisterRealNameRequestValidator()
    {
        // 長度以 Unicode 字元（rune）計算，與 PostgreSQL varchar(50) 的字元計數一致：
        // 若用 string.Length（UTF-16 單位），每字佔兩個單位的擴充 B 區罕用字姓名超過 25 字就會被誤擋。
        // 格式字元（Cf，例如零寬空格 U+200B、方向覆寫 U+202E）與少數歸類為字母／符號卻顯示為空白的字元看不見，
        // 會讓核銷人員比對證件時看到空白或順序顛倒的姓名（RNV-FORMAT-007／RNV-FORMAT-009）。
        RuleFor(x => x.RealName)
            .Cascade(CascadeMode.Stop)
            .Must(realName => !string.IsNullOrWhiteSpace(realName)).WithMessage("真實姓名為必填。")
            .Must(realName => realName.Trim().EnumerateRunes().Count() <= RealNameMaxLength).WithMessage($"真實姓名不得超過 {RealNameMaxLength} 字。")
            .Must(realName => !realName.EnumerateRunes().Any(IsInvisibleCharacter)).WithMessage("真實姓名不得包含控制字元或不可見的字元。")
            // 黑名單列不完整（例如只由組合字元 U+034F、異體字選擇符 U+FE0F 組成的姓名同樣看不見），
            // 再要求至少一個字母類別（L*）的字元，確保核銷面板有可比對的文字（RNV-FORMAT-010）。
            .Must(realName => realName.EnumerateRunes().Any(Rune.IsLetter)).WithMessage("真實姓名至少須包含一個文字。");

        // 用 \z 而非 $：.NET 的 $ 允許字串尾端多一個 \n，"1234\n" 會通過。[0-9] 而非 \d：\d 會接受全形數字。
        RuleFor(x => x.NationalIdLast4)
            .Must(nationalIdLast4 => nationalIdLast4 is not null && Regex.IsMatch(nationalIdLast4, @"^[0-9]{4}\z"))
            .WithMessage("身分證末四碼必須為 4 個半形數字。");
    }

    // 這些字元的 Unicode 類別是 Lo／So，不在 Cc／Cf 內，但字型一律顯示為空白：韓文填充字與空白點字。
    private static readonly HashSet<int> BlankLookingCodePoints = [0x115F, 0x1160, 0x2800, 0x3164, 0xFFA0];

    private static bool IsInvisibleCharacter(Rune rune)
        => Rune.IsControl(rune)
            || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format
            // U+2028／U+2029 不是 Cc，但會強制換行，與換行字元同樣被拒（RNV-FORMAT-004）。
            || Rune.GetUnicodeCategory(rune) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
            || BlankLookingCodePoints.Contains(rune.Value);
}
