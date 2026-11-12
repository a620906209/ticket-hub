using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using ProjectC.WebApi.Tests.TestSupport;
using static ProjectC.WebApi.Tests.TestSupport.ForwardedHeadersWebApplicationFactory;

namespace ProjectC.WebApi.Tests.ForwardedHeaders;

// client-address-resolution spec CAR-001～004、006～013。以測試 middleware 模擬連線來源，經測試端點觀察
// 還原後的 RemoteIpAddress／Scheme；防的是「受信任代理的標頭沒被還原（限流仍共用代理位址）」與
// 「非信任來源能偽造位址繞過限流」兩種後果（reverse-proxy-forwarded-headers design.md 決策 7）。
public class ForwardedHeadersMiddlewareTests : IClassFixture<ForwardedHeadersWebApplicationFactory>
{
    private const string TrustedConnectionIp = "10.0.0.5";
    private const string UntrustedConnectionIp = "198.51.100.20";
    private const string ClientIp = "203.0.113.7";

    private readonly ForwardedHeadersWebApplicationFactory _factory;

    public ForwardedHeadersMiddlewareTests(ForwardedHeadersWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<ClientAddressProbeResponse> ProbeAsync(
        WebApplicationFactory<Program> factory,
        string connectionIp,
        string? forwardedFor = null,
        string? forwardedProto = null,
        string? forwardedHost = null)
    {
        using var client = CreateNonRedirectingClient(factory);
        using var request = CreateRequest(ProbePath, connectionIp, forwardedFor, forwardedProto, forwardedHost);
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<ClientAddressProbeResponse>())!;
    }

    private Task<ClientAddressProbeResponse> ProbeAsync(
        string connectionIp,
        string? forwardedFor = null,
        string? forwardedProto = null,
        string? forwardedHost = null)
        => ProbeAsync(_factory, connectionIp, forwardedFor, forwardedProto, forwardedHost);

    private WebApplicationFactory<Program> CreateFactoryWithConfiguration(Dictionary<string, string?> overrides)
        => _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configBuilder) => configBuilder.AddInMemoryCollection(overrides)));

    private static bool IsRedirect(HttpResponseMessage response) => (int)response.StatusCode is >= 300 and <= 399;

    // CAR-001、CAR-005（預設網段）
    [Fact]
    public async Task Request_FromTrustedSourceWithForwardedFor_RestoresClientAddress()
    {
        var probe = await ProbeAsync(TrustedConnectionIp, forwardedFor: ClientIp);

        probe.RemoteIpAddress.Should().Be(ClientIp);
    }

    // CAR-002
    [Fact]
    public async Task Request_FromUntrustedSourceWithForwardedFor_IgnoresTheHeader()
    {
        var probe = await ProbeAsync(UntrustedConnectionIp, forwardedFor: ClientIp);

        probe.RemoteIpAddress.Should().Be(UntrustedConnectionIp);
    }

    // CAR-003
    [Fact]
    public async Task Request_FromTrustedSourceWithMultipleForwardedFor_UsesOnlyTheLastValue()
    {
        var probe = await ProbeAsync(TrustedConnectionIp, forwardedFor: $"198.51.100.9, {ClientIp}");

        probe.RemoteIpAddress.Should().Be(ClientIp);
    }

    // CAR-004
    [Fact]
    public async Task Request_FromTrustedSourceWithoutForwardedFor_KeepsConnectionAddress()
    {
        var probe = await ProbeAsync(TrustedConnectionIp);

        probe.RemoteIpAddress.Should().Be(TrustedConnectionIp);
    }

    // CAR-009：最後一個值無法解析時不得失敗，且位址維持連線位址（落入與未帶標頭相同的限流分區）。
    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData("203.0.113.7, garbage")]
    public async Task Request_FromTrustedSourceWithUnparsableForwardedFor_KeepsConnectionAddress(string forwardedFor)
    {
        var probe = await ProbeAsync(TrustedConnectionIp, forwardedFor: forwardedFor);

        probe.RemoteIpAddress.Should().Be(TrustedConnectionIp);
    }

    [Fact]
    public async Task Request_FromTrustedSourceWithOverlongForwardedFor_KeepsConnectionAddress()
    {
        var probe = await ProbeAsync(TrustedConnectionIp, forwardedFor: new string('a', 5000));

        probe.RemoteIpAddress.Should().Be(TrustedConnectionIp);
    }

    // CAR-011：預期值依實測框架行為而定（見 tasks.md 1.2 紀錄）。
    [Fact]
    public async Task Request_FromTrustedSourceWithLeadingGarbageAndValidLastValue_RestoresTheLastValue()
    {
        var probe = await ProbeAsync(TrustedConnectionIp, forwardedFor: $"garbage, {ClientIp}");

        probe.RemoteIpAddress.Should().Be(ClientIp);
    }

    [Fact]
    public async Task Request_FromTrustedSourceWithAddressAndPort_RestoresTheAddress()
    {
        var probe = await ProbeAsync(TrustedConnectionIp, forwardedFor: $"{ClientIp}:8080");

        probe.RemoteIpAddress.Should().Be(ClientIp);
    }

    // 實測記錄：IPv4-mapped IPv6 形式的連線來源是否視為受信任（雙堆疊 socket 可能回報此形式）。
    [Fact]
    public async Task Request_FromIpv4MappedIpv6TrustedSource_IsTreatedAsTrusted()
    {
        var probe = await ProbeAsync("::ffff:10.0.0.5", forwardedFor: ClientIp);

        probe.RemoteIpAddress.Should().Be(ClientIp);
    }

    // CAR-012
    [Fact]
    public async Task Request_FromTrustedSourceWithForwardedHost_DoesNotRewriteHost()
    {
        var probe = await ProbeAsync(TrustedConnectionIp, forwardedHost: "evil.example");

        probe.Host.Should().Be("localhost");
    }

    // CAR-010
    [Fact]
    public async Task Request_FromTrustedSourceWithForwardedProtoHttps_RestoresScheme()
    {
        var probe = await ProbeAsync(TrustedConnectionIp, forwardedProto: "https");

        probe.Scheme.Should().Be("https");
    }

    [Fact]
    public async Task Request_FromUntrustedSourceWithForwardedProtoHttps_KeepsConnectionScheme()
    {
        var probe = await ProbeAsync(UntrustedConnectionIp, forwardedProto: "https");

        probe.Scheme.Should().Be("http");
    }

    // CAR-006
    [Fact]
    public async Task Request_WithCustomTrustedNetworks_IgnoresHeaderFromDefaultNetworkOutsideTheCustomList()
    {
        using var customFactory = CreateFactoryWithConfiguration(new() { ["ForwardedHeaders:TrustedNetworks:0"] = "10.1.0.0/16" });

        var outside = await ProbeAsync(customFactory, "192.168.1.5", forwardedFor: ClientIp);
        var inside = await ProbeAsync(customFactory, "10.1.2.3", forwardedFor: ClientIp);

        outside.RemoteIpAddress.Should().Be("192.168.1.5");
        inside.RemoteIpAddress.Should().Be(ClientIp, "對照：自訂網段內的來源仍會被還原");
    }

    // CAR-007
    [Theory]
    [InlineData("not-a-cidr")]
    [InlineData("")]
    public void CreatingHost_WithInvalidTrustedNetworksEntry_FailsNamingTheConfigurationKey(string entry)
    {
        using var failingFactory = CreateFactoryWithConfiguration(new() { ["ForwardedHeaders:TrustedNetworks:0"] = entry });

        var act = () => failingFactory.Server;

        act.Should().Throw<Exception>().Where(exception => MessageChainContains(exception, "ForwardedHeaders:TrustedNetworks"));
    }

    [Fact]
    public void CreatingHost_WithExplicitlyEmptyTrustedNetworks_FailsNamingTheConfigurationKey()
    {
        using var failingFactory = CreateFactoryWithConfiguration(new() { ["ForwardedHeaders:TrustedNetworks"] = "" });

        var act = () => failingFactory.Server;

        act.Should().Throw<Exception>().Where(exception => MessageChainContains(exception, "ForwardedHeaders:TrustedNetworks"));
    }

    private static bool MessageChainContains(Exception exception, string text)
    {
        Exception? current = exception;
        while (current is not null)
        {
            if (current.Message.Contains(text, StringComparison.Ordinal))
            {
                return true;
            }

            current = current.InnerException;
        }

        return false;
    }

    // CAR-008、CAR-013：須設定 HTTPS 轉址連接埠，否則 UseHttpsRedirection 本來就不轉址，測試會空轉；
    // 因此每個斷言都配對照組，證明轉址確實生效。
    [Fact]
    public async Task HttpsRedirection_ControlRequestsWithoutRestoredHttps_AreRedirected()
    {
        using var httpsFactory = CreateFactoryWithConfiguration(new() { ["HTTPS_PORT"] = "443" });
        using var client = CreateNonRedirectingClient(httpsFactory);

        using var noHeader = await client.SendAsync(CreateRequest("/api/events", TrustedConnectionIp));
        using var untrustedWithHttps = await client.SendAsync(CreateRequest("/api/events", UntrustedConnectionIp, forwardedProto: "https"));
        using var trustedWithHttp = await client.SendAsync(CreateRequest("/api/events", TrustedConnectionIp, forwardedProto: "http"));

        IsRedirect(noHeader).Should().BeTrue("無標頭的請求被轉址，證明 HTTPS_PORT 已生效");
        IsRedirect(untrustedWithHttps).Should().BeTrue("非信任來源的 X-Forwarded-Proto 被忽略");
        IsRedirect(trustedWithHttp).Should().BeTrue();
    }

    // CAR-013
    [Fact]
    public async Task HttpsRedirection_ForTrustedSourceWithForwardedProtoHttps_IsNotRedirected()
    {
        using var httpsFactory = CreateFactoryWithConfiguration(new() { ["HTTPS_PORT"] = "443" });
        using var client = CreateNonRedirectingClient(httpsFactory);

        using var response = await client.SendAsync(CreateRequest("/api/events", TrustedConnectionIp, forwardedProto: "https"));

        IsRedirect(response).Should().BeFalse();
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // CAR-008
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthProbe_FromTrustedSourceWithForwardedProtoHttp_IsNotRedirected(string path)
    {
        using var httpsFactory = CreateFactoryWithConfiguration(new() { ["HTTPS_PORT"] = "443" });
        using var client = CreateNonRedirectingClient(httpsFactory);

        using var response = await client.SendAsync(CreateRequest(path, TrustedConnectionIp, forwardedProto: "http"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Location.Should().BeNull();
    }
}
