using System.Net.Http.Json;
using FluentAssertions;
using ProjectC.WebApi.Tests.TestSupport;
using static ProjectC.WebApi.Tests.TestSupport.ForwardedHeadersWebApplicationFactory;

namespace ProjectC.WebApi.Tests.Startup;

// edge-security-headers spec ESH-009、ESH-013。特性測試：防的是日後有人為了「補齊安全標頭」在 API 加 UseHsts
// （與 nginx 重複送出），或為了「方便前端」開放 CORS，兩者都違反邊界決策（design.md 決策 3、5）。
public class EdgeBoundaryNoCorsNoHstsTests : IClassFixture<ForwardedHeadersWebApplicationFactory>
{
    private const string TrustedProxyIp = "10.0.0.5";
    private const string ForeignOrigin = "https://evil.example";
    private const string NonLoopbackHost = "app.example";

    private readonly ForwardedHeadersWebApplicationFactory _factory;

    public EdgeBoundaryNoCorsNoHstsTests(ForwardedHeadersWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static bool HasAccessControlAllowHeader(HttpResponseMessage response)
        => response.Headers.Concat(response.Content.Headers)
            .Any(header => header.Key.StartsWith("Access-Control-Allow-", StringComparison.OrdinalIgnoreCase));

    // ESH-013
    [Fact]
    public async Task GetHealthLive_WhenCrossOriginRequest_HasNoAccessControlAllowHeaders()
    {
        using var client = CreateNonRedirectingClient(_factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.TryAddWithoutValidation("Origin", ForeignOrigin);

        using var response = await client.SendAsync(request);

        response.IsSuccessStatusCode.Should().BeTrue();
        HasAccessControlAllowHeader(response).Should().BeFalse();
    }

    // ESH-013
    [Fact]
    public async Task Options_WhenPreflightFromForeignOrigin_HasNoAccessControlAllowHeaders()
    {
        using var client = CreateNonRedirectingClient(_factory);
        using var request = new HttpRequestMessage(HttpMethod.Options, "/health/live");
        request.Headers.TryAddWithoutValidation("Origin", ForeignOrigin);
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "GET");

        using var response = await client.SendAsync(request);

        HasAccessControlAllowHeader(response).Should().BeFalse();
    }

    // ESH-009：HstsMiddleware 預設排除 loopback 主機、且只在 IsHttps 時輸出，所以請求須用非 loopback 的 Host，
    // 並經 UseForwardedHeaders 還原為 https；先以探針確認前提成立，否則 UseHsts 被加入後此測試也不會失敗。
    [Fact]
    public async Task GetApiEvents_WhenForwardedProtoHttpsFromTrustedProxy_HasNoStrictTransportSecurity()
    {
        using var client = CreateNonRedirectingClient(_factory);

        using var probeRequest = CreateRequest(ProbePath, TrustedProxyIp, forwardedProto: "https");
        probeRequest.Headers.Host = NonLoopbackHost;
        using var probeResponse = await client.SendAsync(probeRequest);
        var probe = await probeResponse.Content.ReadFromJsonAsync<ClientAddressProbeResponse>();
        probe!.Scheme.Should().Be("https", "前提：X-Forwarded-Proto 須被還原，否則本測試無法偵測 UseHsts");

        using var request = CreateRequest("/api/events", TrustedProxyIp, forwardedProto: "https");
        request.Headers.Host = NonLoopbackHost;
        using var response = await client.SendAsync(request);

        response.Headers.Contains("Strict-Transport-Security").Should().BeFalse();
    }
}
