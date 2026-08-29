using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using ProjectC.WebApi.Common;

namespace ProjectC.WebApi.Tests.Auth;

// api-rate-limiting spec LRL-004：只驗證分區鍵推導邏輯本身，不是端到端驗證——WebApplicationFactory/
// TestServer 的 in-memory transport 對所有請求回報固定 RemoteIpAddress，無法透過建立多個 HttpClient
// 模擬不同來源 IP（login-rate-limiting design.md 決策 3、tasks.md 4.1）。
public class LoginRateLimiterPartitioningTests
{
    [Fact]
    public void GetPartitionKey_WithSameRemoteIpAddress_ReturnsSameKey()
    {
        var contextA = new DefaultHttpContext();
        contextA.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
        var contextB = new DefaultHttpContext();
        contextB.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");

        var keyA = LoginRateLimiterPartitioning.GetPartitionKey(contextA);
        var keyB = LoginRateLimiterPartitioning.GetPartitionKey(contextB);

        keyA.Should().Be(keyB);
    }

    [Fact]
    public void GetPartitionKey_WithDifferentRemoteIpAddress_ReturnsDifferentKey()
    {
        var contextA = new DefaultHttpContext();
        contextA.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
        var contextB = new DefaultHttpContext();
        contextB.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.20");

        var keyA = LoginRateLimiterPartitioning.GetPartitionKey(contextA);
        var keyB = LoginRateLimiterPartitioning.GetPartitionKey(contextB);

        keyA.Should().NotBe(keyB);
    }

    [Fact]
    public void GetPartitionKey_WithNullRemoteIpAddress_ReturnsUnknownWithoutThrowing()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = null;

        var act = () => LoginRateLimiterPartitioning.GetPartitionKey(context);

        act.Should().NotThrow();
        act().Should().Be("unknown");
    }
}
