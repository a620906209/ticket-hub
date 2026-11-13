using Npgsql;

namespace ProjectC.WebApi.ExceptionHandling;

// 命令逾時與連線池耗盡等待逾時，實測都是 NpgsqlException 內含 TimeoutException；連線池耗盡外層另被 EF Core Npgsql
// 包成 InvalidOperationException，故沿整條例外鏈找，不只看最外層（db-timeout-pool-tuning design.md 決策 3）。
// 沒有被 NpgsqlException 包住的裸 TimeoutException（例如 HttpClient 逾時）不是資料庫逾時。
public static class DatabaseTimeoutDetector
{
    public static bool IsDatabaseTimeout(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return EnumerateExceptionGraph(exception)
            .Any(candidate => candidate is NpgsqlException
                && EnumerateExceptionGraph(candidate.InnerException).Any(inner => inner is TimeoutException));
    }

    // 以 visited 集合防止循環參照造成無窮迴圈；AggregateException 的每個內層都要走。
    private static IEnumerable<Exception> EnumerateExceptionGraph(Exception? root)
    {
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<Exception>();
        if (root is not null)
        {
            pending.Push(root);
        }

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            yield return current;

            if (current is AggregateException aggregateException)
            {
                foreach (var inner in aggregateException.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }
    }
}
