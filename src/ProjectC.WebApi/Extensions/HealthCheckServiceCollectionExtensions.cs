using ProjectC.WebApi.HealthChecks;

namespace ProjectC.WebApi.Extensions;

public static class HealthCheckServiceCollectionExtensions
{
    public static IServiceCollection AddApiHealthChecks(this IServiceCollection services)
    {
        // readiness 只檢查 DB：Redis 是所有 Pod 共用的相依，納入判定會把可降級的功能變成全站中斷
        // （api-health-checks design.md 決策 2）。
        services.AddHealthChecks()
            .AddCheck<DatabaseReadinessHealthCheck>("database", tags: [HealthCheckPaths.ReadyTag], timeout: TimeSpan.FromSeconds(DatabaseReadinessHealthCheck.TimeoutSeconds));

        return services;
    }
}
