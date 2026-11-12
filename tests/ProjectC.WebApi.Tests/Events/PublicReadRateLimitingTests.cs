using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using ProjectC.Application.Authentication.Login;
using ProjectC.WebApi.Tests.TestSupport;
using static ProjectC.WebApi.Tests.TestSupport.ForwardedHeadersWebApplicationFactory;

namespace ProjectC.WebApi.Tests.Events;

// api-rate-limiting spec PRL-001～004。整個類別共用一個 factory（避免每個測試啟動一組容器），
// 視窗長於整個測試執行時間，各測試改用互不重複的用戶端 IP，各自落在獨立的限流分區。
// 防的是匿名公開讀取端點被單一來源 IP 無限打到資料庫，以及限流誤傷 login／captcha／探針。
public class PublicReadRateLimitingTests : IClassFixture<PublicReadRateLimitedWebApplicationFactory>
{
    private static int _nextClientIpSuffix = 10;
    private readonly PublicReadRateLimitedWebApplicationFactory _factory;
    private const string TrustedProxyIp = "10.0.0.5";

    public PublicReadRateLimitingTests(PublicReadRateLimitedWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static string NewClientIp() => $"203.0.113.{Interlocked.Increment(ref _nextClientIpSuffix)}";

    private static string UnknownEventPath() => $"/api/events/{Guid.NewGuid()}";

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string clientIp)
    {
        using var request = CreateRequest(path, TrustedProxyIp, forwardedFor: clientIp);
        return await client.SendAsync(request);
    }

    private static async Task ExhaustQuotaAsync(HttpClient client, string clientIp)
    {
        for (var i = 0; i < PublicReadRateLimitedWebApplicationFactory.PermitLimit; i++)
        {
            using var response = await GetAsync(client, "/api/events", clientIp);
            response.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        }
    }

    [Fact]
    public async Task GetEvents_OverThePermitLimit_ReturnsProblemDetailsWithRetryAfter()
    {
        using var client = CreateNonRedirectingClient(_factory);
        var clientA = NewClientIp();
        await ExhaustQuotaAsync(client, clientA);

        using var response = await GetAsync(client, "/api/events", clientA);

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        response.Headers.Contains("Retry-After").Should().BeTrue();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("title").GetString().Should().Be("TooManyRequests");
    }

    [Fact]
    public async Task GetEvents_WhenAnotherClientIpIsExhausted_StillServesDifferentClientIp()
    {
        using var client = CreateNonRedirectingClient(_factory);
        var clientA = NewClientIp();
        await ExhaustQuotaAsync(client, clientA);

        var clientB = NewClientIp();
        using var exhausted = await GetAsync(client, "/api/events", clientA);
        using var other = await GetAsync(client, "/api/events", clientB);

        exhausted.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        other.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("detail")]
    [InlineData("seats")]
    [InlineData("ticket-types")]
    public async Task OtherPublicEventEndpoints_AfterListExhaustedQuota_ShareTheSameQuota(string endpoint)
    {
        using var client = CreateNonRedirectingClient(_factory);
        var clientA = NewClientIp();
        await ExhaustQuotaAsync(client, clientA);
        var path = endpoint == "detail" ? UnknownEventPath() : $"{UnknownEventPath()}/{endpoint}";

        using var response = await GetAsync(client, path, clientA);

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    // 詳情端點走列表快取，無法以 Repository 呼叫次數觀察；seats 端點每次都查 GetByIdAsync，作為「429 時查詢未執行」的觀察點。
    [Fact]
    public async Task GetEventSeats_WhenRateLimited_DoesNotQueryTheRepository()
    {
        using var client = CreateNonRedirectingClient(_factory);
        var clientA = NewClientIp();
        var callsAtStart = _factory.EventRepositoryCallCounter.GetByIdAsyncCallCount;
        for (var i = 0; i < PublicReadRateLimitedWebApplicationFactory.PermitLimit; i++)
        {
            using var allowed = await GetAsync(client, $"{UnknownEventPath()}/seats", clientA);
            allowed.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }


        var callsBefore = _factory.EventRepositoryCallCounter.GetByIdAsyncCallCount;
        (callsBefore - callsAtStart).Should().Be(PublicReadRateLimitedWebApplicationFactory.PermitLimit);

        using var limited = await GetAsync(client, $"{UnknownEventPath()}/seats", clientA);

        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        _factory.EventRepositoryCallCounter.GetByIdAsyncCallCount.Should().Be(callsBefore);
    }

    [Fact]
    public async Task CaptchaAndLogin_AfterPublicReadQuotaExhausted_AreNotRateLimited()
    {
        using var client = CreateNonRedirectingClient(_factory);
        var clientA = NewClientIp();
        await ExhaustQuotaAsync(client, clientA);

        using var captcha = await GetAsync(client, "/api/captcha", clientA);
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest(AuthTestHelper.NewEmail(), "WrongPassword1", FakeCaptchaService.ValidToken, FakeCaptchaService.ValidAnswer)),
        };
        loginRequest.Headers.TryAddWithoutValidation(ConnectionIpHeader, TrustedProxyIp);
        loginRequest.Headers.TryAddWithoutValidation("X-Forwarded-For", clientA);
        using var login = await client.SendAsync(loginRequest);

        captcha.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        login.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task HealthLive_AfterPublicReadQuotaExhausted_IsNotRateLimited()
    {
        using var client = CreateNonRedirectingClient(_factory);
        var clientA = NewClientIp();
        await ExhaustQuotaAsync(client, clientA);

        using var response = await GetAsync(client, "/health/live", clientA);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
