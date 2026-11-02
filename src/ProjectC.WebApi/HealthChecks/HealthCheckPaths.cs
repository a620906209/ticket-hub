namespace ProjectC.WebApi.HealthChecks;

// 端點對應、日誌等級與 HTTPS 轉址排除共用同一份定義，避免三處字串不同步（api-health-checks design.md 決策 6）。
public static class HealthCheckPaths
{
    public const string Live = "/health/live";
    public const string Ready = "/health/ready";
    public const string ReadyTag = "ready";

    public static bool IsProbePath(PathString path)
        => path.Equals(Live, StringComparison.OrdinalIgnoreCase)
            || path.Equals(Ready, StringComparison.OrdinalIgnoreCase);
}
