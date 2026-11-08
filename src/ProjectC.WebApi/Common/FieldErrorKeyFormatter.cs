namespace ProjectC.WebApi.Common;

// Handler 路徑（FluentValidation 的 PropertyName）與模型繫結路徑（ModelState 鍵）共用，
// 兩條路徑的 errors 鍵規則才不會漂移（validation-error-fields design.md 決策 2）。
public static class FieldErrorKeyFormatter
{
    private const string JsonPathRoot = "$";
    private const string JsonPathPrefix = "$.";

    public static string Format(string key)
    {
        if (key == JsonPathRoot || key.Length == 0)
        {
            return string.Empty;
        }

        var path = key.StartsWith(JsonPathPrefix, StringComparison.Ordinal) ? key[JsonPathPrefix.Length..]
            : key.StartsWith("$[", StringComparison.Ordinal) ? key[JsonPathRoot.Length..]
            : key;

        // 括號段（['a.b']）由 client 送出含 '.' 或空白的屬性名時產生，System.Text.Json 以此表示路徑；
        // 括號內的 '.' 不是分隔符，且鍵名原樣保留（不改大小寫）。
        var builder = new System.Text.StringBuilder(path.Length);
        var isInsideBracket = false;
        var isSegmentStart = true;
        for (var index = 0; index < path.Length; index++)
        {
            var current = path[index];
            if (isInsideBracket)
            {
                builder.Append(current);
                isInsideBracket = !(current == ']' && path[index - 1] == '\'');
                continue;
            }

            if (current == '[' && index + 1 < path.Length && path[index + 1] == '\'')
            {
                isInsideBracket = true;
            }

            builder.Append(isSegmentStart ? char.ToLowerInvariant(current) : current);
            isSegmentStart = current == '.';
        }

        return builder.ToString();
    }

    // 兩個原始鍵轉換後相同時合併訊息而不是覆寫，避免靜默丟失錯誤。
    public static Dictionary<string, string[]> FormatAll(IEnumerable<KeyValuePair<string, string[]>> fieldErrors)
    {
        return fieldErrors
            .GroupBy(pair => Format(pair.Key))
            .ToDictionary(group => group.Key, group => group.SelectMany(pair => pair.Value).ToArray());
    }
}
