using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using ProjectC.Infrastructure.Persistence;

namespace ProjectC.WebApi.HealthChecks;

// 不用 AddDbContextCheck：它沒有 timeout 參數，且要新增套件（api-health-checks design.md 決策 1）。
public sealed class DatabaseReadinessHealthCheck : IHealthCheck
{
    public const int TimeoutSeconds = 2;

    private readonly ApplicationDbContext _dbContext;

    public DatabaseReadinessHealthCheck(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // 實測 DB 無回應時，registration 的 token 無法讓 check 準時返回，建立新連線時要靠連線 Timeout 結束等待；
        // 關閉連線池是因為重用閒置連線時連線 Timeout 不起作用，實測會卡 34 秒。
        // 這個 DbContext 只活在本次檢查的 scope，不影響其他 DB 操作（api-health-checks design.md 決策 3）。
        _dbContext.Database.SetConnectionString(new NpgsqlConnectionStringBuilder(_dbContext.Database.GetConnectionString())
        {
            Timeout = TimeoutSeconds,
            Pooling = false
        }.ConnectionString);

        var canConnect = await _dbContext.Database.CanConnectAsync(cancellationToken);

        return canConnect ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy();
    }
}
