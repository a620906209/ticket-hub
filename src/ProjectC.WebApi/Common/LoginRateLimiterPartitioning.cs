namespace ProjectC.WebApi.Common;

// 抽成獨立、可直接建構 DefaultHttpContext 呼叫的 public static 方法，不沿用既有 CreateMemberPartition
// 那種內嵌於 Program.cs 的 local function 寫法——WebApplicationFactory/TestServer 的 in-memory
// transport 對所有請求回報固定的 RemoteIpAddress，無法透過建立多個 HttpClient 模擬不同來源 IP，
// 分區鍵推導邏輯本身需要能被單元測試直接驗證（login-rate-limiting design.md 決策 3）。
public static class LoginRateLimiterPartitioning
{
    public static string GetPartitionKey(HttpContext httpContext)
        => httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
