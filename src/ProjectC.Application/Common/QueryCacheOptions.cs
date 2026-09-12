using System.ComponentModel.DataAnnotations;

namespace ProjectC.Application.Common;

// fail-fast 驗證，不當作有安全預設值的設定：刻意不給 C# 層級預設值，缺漏時維持 0，會被下方
// [Range] 擋下、與「設定但為 0 或負數」得到同樣的 fail-fast 效果（query-caching design.md 決策 6a）。
public sealed class QueryCacheOptions
{
    public const string SectionName = "QueryCache";

    [Range(1, int.MaxValue)]
    public int EventListTtlSeconds { get; set; }

    [Range(1, int.MaxValue)]
    public int TicketTypesTtlSeconds { get; set; }
}
