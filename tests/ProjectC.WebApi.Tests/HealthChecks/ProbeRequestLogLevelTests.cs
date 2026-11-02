using FluentAssertions;
using Microsoft.AspNetCore.Http;
using ProjectC.WebApi.HealthChecks;
using Serilog.Events;

namespace ProjectC.WebApi.Tests.HealthChecks;

// 只有成功的探測可以降級：失敗的探測若被一起降級，Pod 被移出 Service 時預設等級下會查不到原因。
public class ProbeRequestLogLevelTests
{
    // 499 為呼叫端中斷，實機會出現；404 為路由設定錯誤。兩者都不是 200，也不是 5xx。
    [Theory]
    [InlineData(499)]
    [InlineData(404)]
    public void GetLevel_WhenProbeReturnsNonOkBelow500_ReturnsError(int statusCode)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = HealthCheckPaths.Ready;
        httpContext.Response.StatusCode = statusCode;

        ProbeRequestLogLevel.GetLevel(httpContext, 1, null).Should().Be(LogEventLevel.Error);
    }

    // kubelet 以外的呼叫端可能用不同大小寫，ASP.NET Core 路由仍會命中，比對須與路由一致。
    [Theory]
    [InlineData("/HEALTH/READY")]
    [InlineData("/Health/Live")]
    public void IsProbePath_WhenCaseDiffers_ReturnsTrue(string path)
    {
        HealthCheckPaths.IsProbePath(path).Should().BeTrue();
    }

    // design 決策 6：放寬比對會讓其他路徑跳過 HTTPS 轉址，並把它們的 404 記成 Error。
    [Theory]
    [InlineData("/health")]
    [InlineData("/health/foo")]
    [InlineData("/health/live/")]
    public void IsProbePath_WhenPathIsNotExactProbePath_ReturnsFalse(string path)
    {
        HealthCheckPaths.IsProbePath(path).Should().BeFalse();
    }
}
