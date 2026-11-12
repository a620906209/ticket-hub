using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using ProjectC.Application.Authentication.Login;
using ProjectC.WebApi.Tests.TestSupport;
using static ProjectC.WebApi.Tests.TestSupport.ForwardedHeadersWebApplicationFactory;

namespace ProjectC.WebApi.Tests.Auth;

// api-rate-limiting spec LRL-011、LRL-012（login）與 captcha 的對應情境。
// 防的是兩種後果：受信任代理後所有用戶端共用代理位址的同一個額度（一人超額全站被擋），
// 以及非信任來源以偽造 X-Forwarded-For 每次換一個位址繞過限流。
// 整個類別共用一個 factory，各測試用互不重複的連線 IP／用戶端 IP，落入獨立分區。
public class ClientIpRateLimitingTests : IClassFixture<ClientIpRateLimitedWebApplicationFactory>
{
    private const string TrustedProxyIp = "10.0.0.5";
    private static int _nextAddressSuffix = 10;

    private readonly ClientIpRateLimitedWebApplicationFactory _factory;

    public ClientIpRateLimitingTests(ClientIpRateLimitedWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static string NewClientIp() => $"203.0.113.{Interlocked.Increment(ref _nextAddressSuffix)}";

    private static string NewUntrustedConnectionIp() => $"198.51.100.{Interlocked.Increment(ref _nextAddressSuffix)}";

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string endpoint, string connectionIp, string forwardedFor)
    {
        var request = endpoint == "login"
            ? new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
            {
                Content = JsonContent.Create(new LoginRequest(AuthTestHelper.NewEmail(), "WrongPassword1", FakeCaptchaService.ValidToken, FakeCaptchaService.ValidAnswer)),
            }
            : new HttpRequestMessage(HttpMethod.Get, "/api/captcha");
        request.Headers.TryAddWithoutValidation(ConnectionIpHeader, connectionIp);
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        return client.SendAsync(request);
    }

    private static async Task ExhaustQuotaAsync(HttpClient client, string endpoint, string connectionIp, Func<string> nextForwardedFor)
    {
        for (var i = 0; i < ClientIpRateLimitedWebApplicationFactory.PermitLimit; i++)
        {
            using var response = await SendAsync(client, endpoint, connectionIp, nextForwardedFor());
            response.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        }
    }

    [Theory]
    [InlineData("login")]
    [InlineData("captcha")]
    public async Task Request_ThroughTrustedProxy_CountsEachClientIpSeparately(string endpoint)
    {
        using var client = CreateNonRedirectingClient(_factory);
        var clientA = NewClientIp();
        var clientB = NewClientIp();
        await ExhaustQuotaAsync(client, endpoint, TrustedProxyIp, () => clientA);

        using var exhausted = await SendAsync(client, endpoint, TrustedProxyIp, clientA);
        using var other = await SendAsync(client, endpoint, TrustedProxyIp, clientB);

        exhausted.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        other.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
    }

    [Theory]
    [InlineData("login")]
    [InlineData("captcha")]
    public async Task Request_FromUntrustedSourceWithForgedForwardedFor_SharesOnePartition(string endpoint)
    {
        using var client = CreateNonRedirectingClient(_factory);
        var untrustedConnectionIp = NewUntrustedConnectionIp();
        await ExhaustQuotaAsync(client, endpoint, untrustedConnectionIp, NewClientIp);

        using var response = await SendAsync(client, endpoint, untrustedConnectionIp, NewClientIp());

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }
}
