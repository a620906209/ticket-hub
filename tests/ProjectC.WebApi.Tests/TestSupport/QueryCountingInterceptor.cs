using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ProjectC.WebApi.Tests.TestSupport;

/// <summary>
/// 計算實際送到資料庫的查詢（order-display-enrichment tasks.md 1.3）。單元測試只能數 Fake repository 被呼叫幾次，
/// repository 實作內部若逐筆查詢仍會通過，「查詢次數不隨筆數成長」只能在這一層驗證。
/// 只記錄帶有 <see cref="TagHeaderName"/> 標頭的 HTTP 請求所發出的查詢，並依標頭值分組：背景服務
/// （購票佇列、逾期訂單清理）沒有 HttpContext、不會被計入，避免與被測請求同時發生的查詢干擾計數。
/// </summary>
public sealed partial class QueryCountingInterceptor(IHttpContextAccessor httpContextAccessor) : DbCommandInterceptor
{
    public const string TagHeaderName = "X-Query-Count-Tag";

    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _commandTextsByTag = new();

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Record(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public IReadOnlyList<string> GetCommandTexts(string tag)
        => _commandTextsByTag.TryGetValue(tag, out var commandTexts) ? commandTexts.ToList() : [];

    /// <summary>
    /// 「資料表 → 查詢次數」對照表：每筆查詢中出現的每個帶引號資料表名稱各計一次。以帶引號比對，避免
    /// <c>"Orders"</c> 誤中 <c>"OrderItems"</c>；join 帶出的表（如 <c>"OrderItems"</c>）會與主表同時計入。
    /// </summary>
    public IReadOnlyDictionary<string, int> GetQueryCountsByTable(string tag)
        => GetCommandTexts(tag)
            .SelectMany(commandText => QuotedTableNameRegex().Matches(commandText).Select(match => match.Groups[1].Value).Distinct())
            .GroupBy(tableName => tableName)
            .ToDictionary(group => group.Key, group => group.Count());

    private void Record(DbCommand command)
    {
        var tag = httpContextAccessor.HttpContext?.Request.Headers[TagHeaderName].ToString();
        if (string.IsNullOrEmpty(tag))
        {
            return;
        }

        _commandTextsByTag.GetOrAdd(tag, _ => new ConcurrentQueue<string>()).Enqueue(command.CommandText);
    }

    // 只比對 FROM／JOIN 之後的資料表名稱，排除欄位名稱（例如 "EventId"）與別名。
    [GeneratedRegex("""(?:FROM|JOIN)\s+"(\w+)"\s""", RegexOptions.IgnoreCase)]
    private static partial Regex QuotedTableNameRegex();
}
