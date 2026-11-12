using Microsoft.EntityFrameworkCore;

namespace ProjectC.Infrastructure.Tests.TestSupport;

public static class PostgresLockProbe
{
    /// <summary>
    /// 以「確實有連線在等鎖」作為放行條件，不用固定睡眠（order-placement-p95-optimization design.md 決策 5）。pg_locks 是系統檢視表，
    /// EF Core 沒有對應模型，只能用 SqlQuery；查詢沒有任何外部輸入。等列鎖（FOR UPDATE／FOR SHARE 持有者）的連線是在等持鎖交易的
    /// transactionid，限定 locktype 避免其他種類的鎖等待提早放行；同一個 collection 的測試依序執行，這段期間的等鎖連線只會是該測試的請求。
    /// </summary>
    public static async Task WaitUntilAnotherBackendWaitsForLockAsync(PostgresFixture fixture, TimeSpan timeout)
    {
        await using var probeDbContext = fixture.CreateDbContext();
        using var timeoutSource = new CancellationTokenSource(timeout);
        while (true)
        {
            var waitingLockCount = await probeDbContext.Database
                .SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM pg_locks WHERE locktype = 'transactionid' AND NOT granted AND pid <> pg_backend_pid()""")
                .SingleAsync(timeoutSource.Token);
            if (waitingLockCount > 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), timeoutSource.Token);
        }
    }

    /// <summary>
    /// 等到至少 <paramref name="minimumWaitingBackendCount"/> 條連線在等同一列的 FOR UPDATE：第一個等待者等持鎖交易的 transactionid，
    /// 之後的等待者排在第一個等待者的 tuple 鎖後面，所以兩種 locktype 都要算（只算 transactionid 永遠只會看到 1 條）。
    /// </summary>
    public static async Task WaitUntilBackendsWaitForRowLockAsync(PostgresFixture fixture, TimeSpan timeout, int minimumWaitingBackendCount)
    {
        await using var probeDbContext = fixture.CreateDbContext();
        using var timeoutSource = new CancellationTokenSource(timeout);
        while (true)
        {
            var waitingBackendCount = await probeDbContext.Database
                .SqlQuery<int>($"""SELECT count(DISTINCT pid)::int AS "Value" FROM pg_locks WHERE locktype IN ('transactionid', 'tuple') AND NOT granted AND pid <> pg_backend_pid()""")
                .SingleAsync(timeoutSource.Token);
            if (waitingBackendCount >= minimumWaitingBackendCount)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), timeoutSource.Token);
        }
    }
}
