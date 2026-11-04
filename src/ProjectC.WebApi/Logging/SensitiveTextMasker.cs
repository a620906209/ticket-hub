using System.Text.RegularExpressions;

namespace ProjectC.WebApi.Logging;

/// <summary>
/// 遮蔽例外文字中符合指定格式的敏感值（exception-log-masking design.md 決策 3）。格式集中在單一有序清單，
/// 每條 regex 都設 match timeout；逾時由呼叫端（<see cref="ExceptionMaskingSink"/>）降級處理。
/// </summary>
public static class SensitiveTextMasker
{
    public const int MaxLength = 64 * 1024;
    public const int HeadLength = 32 * 1024;
    public const int TailLength = 32 * 1024;
    public const string TruncationMarker = "\n...[truncated]...\n";
    public const string MaskedValue = "***";

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    // 有引號的值整段取代並保留引號；無引號的 Password/Pwd 值取到 ; 或行尾（連線字串允許未加引號的空白）；
    // 其他 key 的無引號值以空白、; , & ) 與引號為終止。不使用巢狀量詞。
    // 反斜線跳脫的字元視為值的一部分（兩個分支首字元互斥，不會回溯爆炸）。
    // secret/token/api-key 的值若以引號開頭但找不到閉合引號（例如日誌文字被截斷），改取到行尾整段遮蔽，寧可多遮蔽也不外洩。
    private const string QuotedValue = "\"(?:[^\"\\\\\\r\\n]|\\\\.)*\"|'(?:[^'\\\\\\r\\n]|\\\\.)*'";

    private static readonly Rule[] Rules =
    [
        new(
            @"(?<![a-z0-9+.\-])(?<head>[a-z][a-z0-9+.\-]*://[^\s:/@]*:)[^\s@/]+(?<tail>@)",
            match => match.Groups["head"].Value + MaskedValue + match.Groups["tail"].Value),
        new(
            @"(?<head>\bBearer\s+)\S+",
            match => match.Groups["head"].Value + MaskedValue),
        new(
            @"(?<![\w\-])eyJ[\w\-]+\.[\w\-]+\.[\w\-]+",
            _ => MaskedValue),
        new(
            @"(?<key>(?:password|passwd|pwd)[""']?\s*[:=]\s*)(?<value>" + QuotedValue + @"|[^;\r\n]+)",
            MaskKeyedValue),
        new(
            @"(?<key>(?:api[-_]?key|secret|token)[""']?\s*[:=]\s*)(?<value>" + QuotedValue + @"|[""'][^\r\n]*|[^\s;,&)""']+)",
            MaskKeyedValue),
    ];

    public static IReadOnlyList<Regex> Patterns { get; } = Array.AsReadOnly(Rules.Select(rule => rule.Pattern).ToArray());

    /// <summary>
    /// 遮蔽敏感值。超過 <see cref="MaxLength"/> 時保留頭尾各一段、各自獨立遮蔽，中間以
    /// <see cref="TruncationMarker"/> 取代。<see cref="RegexMatchTimeoutException"/> 不在此捕捉。
    /// </summary>
    public static string Mask(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length <= MaxLength)
        {
            return MaskSegment(text);
        }

        var head = TrimHead(text[..HeadLength]);
        var tail = TrimTail(text[^TailLength..]);
        return MaskSegment(head) + TruncationMarker + MaskSegment(tail);
    }

    // 以換行／空白修剪、不以 ; , 修剪：URL 密碼可含這些字元，在其中切開會留下密碼片段。
    private static string TrimHead(string head)
    {
        var cut = head.LastIndexOf('\n');
        if (cut < 0)
        {
            cut = head.LastIndexOf(' ');
        }

        return cut < 0 ? string.Empty : head[..cut];
    }

    private static string TrimTail(string tail)
    {
        var cut = tail.IndexOf('\n');
        return cut < 0 ? string.Empty : tail[(cut + 1)..];
    }

    private static string MaskSegment(string segment)
    {
        var result = segment;
        foreach (var rule in Rules)
        {
            result = rule.Pattern.Replace(result, rule.Evaluator);
        }

        return result;
    }

    private static string MaskKeyedValue(Match match)
    {
        var key = match.Groups["key"].Value;
        var value = match.Groups["value"].Value;
        var isQuoted = value.Length >= 2 && (value[0] is '"' or '\'') && value[^1] == value[0];
        return isQuoted ? key + value[0] + MaskedValue + value[0] : key + MaskedValue;
    }

    private sealed class Rule(string pattern, MatchEvaluator evaluator)
    {
        public Regex Pattern { get; } = new(pattern, Options, MatchTimeout);

        public MatchEvaluator Evaluator { get; } = evaluator;
    }
}
