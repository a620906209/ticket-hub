using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using ProjectC.Application.Authentication.Login;
using ProjectC.Application.Orders.PlaceOrder;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Orders;

// api-rate-limiting spec RL-001~007（rate-limiting-queue design.md 決策 1）。用空 Selections／不存在的
// OrderId 讓請求快速失敗（400／404），只關心是否被限流中介軟體擋下（429），不需要真的準備座位/票種資料。
public class OrdersRateLimitingTests : IClassFixture<RateLimitedWebApplicationFactory>
{
    private readonly RateLimitedWebApplicationFactory _factory;

    public OrdersRateLimitingTests(RateLimitedWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<HttpClient> CreateAuthenticatedMemberClientAsync()
    {
        var client = _factory.CreateClient();
        var tokens = await AuthTestHelper.RegisterAndLoginAsync(client, AuthTestHelper.NewEmail());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    private static Task<HttpResponseMessage> PlaceEmptyOrderAsync(HttpClient client)
        => client.PostAsJsonAsync("/api/orders", new PlaceOrderRequest([]));

    private static Task<HttpResponseMessage> ConfirmNonExistentOrderAsync(HttpClient client)
        => client.PostAsync($"/api/orders/{Guid.NewGuid()}/confirm", null);

    private static Task<HttpResponseMessage> AttemptLoginAsync(HttpClient client)
        => client.PostAsJsonAsync("/api/auth/login", new LoginRequest(AuthTestHelper.NewEmail(), "WrongPassword1"));

    [Fact]
    public async Task PlaceOrder_WithRequestsUnderTheLimit_AllSucceedWithoutBeingRateLimited()
    {
        var client = await CreateAuthenticatedMemberClientAsync();

        for (var i = 0; i < RateLimitedWebApplicationFactory.PermitLimit - 1; i++)
        {
            var response = await PlaceEmptyOrderAsync(client);
            response.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        }
    }

    [Fact]
    public async Task PlaceOrder_WithExactlyThePermitLimitRequests_AllSucceedWithoutBeingRateLimited()
    {
        var client = await CreateAuthenticatedMemberClientAsync();

        for (var i = 0; i < RateLimitedWebApplicationFactory.PermitLimit; i++)
        {
            var response = await PlaceEmptyOrderAsync(client);
            response.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        }
    }

    [Fact]
    public async Task PlaceOrder_WithOneMoreRequestThanThePermitLimit_IsRejectedWith429()
    {
        var client = await CreateAuthenticatedMemberClientAsync();
        for (var i = 0; i < RateLimitedWebApplicationFactory.PermitLimit; i++)
        {
            await PlaceEmptyOrderAsync(client);
        }

        var response = await PlaceEmptyOrderAsync(client);

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task PlaceOrderAndConfirmOrder_UsageOnOneEndpointDoesNotAffectTheOther()
    {
        var client = await CreateAuthenticatedMemberClientAsync();
        for (var i = 0; i < RateLimitedWebApplicationFactory.PermitLimit; i++)
        {
            (await PlaceEmptyOrderAsync(client)).StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        }
        (await PlaceEmptyOrderAsync(client)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "place-order 額度應該已用盡");

        var confirmResponse = await ConfirmNonExistentOrderAsync(client);

        confirmResponse.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, "confirm-order 是獨立計數的 policy，不應受 place-order 額度影響");
    }

    [Fact]
    public async Task PlaceOrder_DifferentMembersAreRateLimitedIndependently()
    {
        var memberAClient = await CreateAuthenticatedMemberClientAsync();
        for (var i = 0; i < RateLimitedWebApplicationFactory.PermitLimit; i++)
        {
            await PlaceEmptyOrderAsync(memberAClient);
        }
        (await PlaceEmptyOrderAsync(memberAClient)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "會員 A 的額度應該已用盡");

        var memberBClient = await CreateAuthenticatedMemberClientAsync();

        var responseForB = await PlaceEmptyOrderAsync(memberBClient);

        responseForB.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, "不同會員的限流額度應該各自獨立");
    }

    [Fact]
    public async Task PlaceOrder_AfterTheWindowResets_RequestsAreAllowedAgain()
    {
        var client = await CreateAuthenticatedMemberClientAsync();
        for (var i = 0; i < RateLimitedWebApplicationFactory.PermitLimit; i++)
        {
            await PlaceEmptyOrderAsync(client);
        }
        (await PlaceEmptyOrderAsync(client)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        await Task.Delay(TimeSpan.FromSeconds(RateLimitedWebApplicationFactory.WindowSeconds + 1));

        var response = await PlaceEmptyOrderAsync(client);

        response.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, "時間窗重置後應該恢復可請求");
    }

    [Fact]
    public async Task PlaceOrder_WhenRateLimited_ReturnsProblemDetailsWithRetryAfterHeader()
    {
        var client = await CreateAuthenticatedMemberClientAsync();
        for (var i = 0; i < RateLimitedWebApplicationFactory.PermitLimit; i++)
        {
            await PlaceEmptyOrderAsync(client);
        }

        var response = await PlaceEmptyOrderAsync(client);

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        response.Headers.Should().ContainKey("Retry-After");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("status").GetInt32().Should().Be(429);
        document.RootElement.TryGetProperty("title", out _).Should().BeTrue();
        document.RootElement.TryGetProperty("traceId", out _).Should().BeTrue();
    }

    // login-rate-limiting spec LRL-006：place-order 額度打滿不影響 login（不同 policy、不同分區鍵
    // 語意——會員 Id vs 來源 IP，計數互不影響）。login 額度繼承自 CustomWebApplicationFactory 的寬鬆
    // 覆寫值（PermitLimit = 1000），所以這裡驗證的是「不受 place-order 用量影響」，不是登入額度本身
    // 寬鬆才通過（見 login-rate-limiting design.md 決策 6）。
    [Fact]
    public async Task PlaceOrderAndLogin_UsageOnPlaceOrderDoesNotAffectLogin()
    {
        var client = await CreateAuthenticatedMemberClientAsync();
        for (var i = 0; i < RateLimitedWebApplicationFactory.PermitLimit; i++)
        {
            (await PlaceEmptyOrderAsync(client)).StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        }

        var loginResponse = await AttemptLoginAsync(client);

        loginResponse.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, "login 是獨立計數的 policy，不應受 place-order 額度影響");
    }
}
