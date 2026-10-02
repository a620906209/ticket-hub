using System.Text.RegularExpressions;

namespace ProjectC.WebApi.Tests.TestSupport;

internal static partial class HexIdentifierText
{
    /// <summary>
    /// 移除 GUID、traceId 這類長度 ≥ 8 的十六進位片段後才斷言「不含末四碼」：隨機 GUID 約有 0.04% 機率
    /// 剛好含測試用的四位數字，造成偶發失敗；真正外洩的末四碼不會落在 8 字元以上的十六進位片段中。
    /// </summary>
    public static string RemoveHexIdentifiers(string text) => HexIdentifierPattern().Replace(text, string.Empty);

    [GeneratedRegex("[0-9a-fA-F-]{8,}")]
    private static partial Regex HexIdentifierPattern();
}
