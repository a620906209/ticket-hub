using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ProjectC.Application.Authentication.Login;
using ProjectC.Application.Events.CreateEvent;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Auth;

// 守護 jwt-challenge-problem-details：JWT 401／403 為含 traceId 的 ProblemDetails，
// WWW-Authenticate 與框架預設逐字相同（基準於 tasks.md 1.1 以同一 factory、固定 exp 錄得）。
public class JwtChallengeProblemDetailsTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string ProblemJsonContentType = "application/problem+json";

    private static readonly DateTime FixedExpiredAtUtc = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly CustomWebApplicationFactory _factory;

    public JwtChallengeProblemDetailsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    public static TheoryData<string, string> UnauthorizedCases => new()
    {
        { "none", "Bearer" },
        { "malformed", "Bearer error=\"invalid_token\"" },
        { "wrongSignature", "Bearer error=\"invalid_token\", error_description=\"The signature key was not found\"" },
        { "expired", "Bearer error=\"invalid_token\", error_description=\"The token expired at '01/01/2020 00:00:00'\"" },
    };

    // [AER-012][AER-013] 四種未通過認證的情境皆為 ProblemDetails，且 header 與框架預設（基準）逐字相同。
    [Theory]
    [MemberData(nameof(UnauthorizedCases))]
    public async Task GetOrders_WhenAuthenticationFails_Returns401ProblemDetailsWithUnchangedChallengeHeader(
        string tokenCase, string expectedWwwAuthenticate)
    {
        var client = _factory.CreateClient();
        var token = CreateToken(tokenCase);
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var response = await client.GetAsync("/api/orders");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType!.MediaType.Should().Be(ProblemJsonContentType);
        response.Headers.GetValues("WWW-Authenticate").Should().ContainSingle().Which.Should().Be(expectedWwwAuthenticate);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertProblemBody(body, 401, "Unauthorized");
    }

    // [AER-017] traceId 取 HttpContext.TraceIdentifier，而非 Activity.Current.Id。
    [Fact]
    public async Task OnChallenge_WithKnownTraceIdentifier_WritesThatTraceId()
    {
        var options = GetJwtBearerOptions();
        var httpContext = CreateHttpContext("trace-challenge-known");
        var context = new JwtBearerChallengeContext(httpContext, CreateScheme(), options, new AuthenticationProperties());

        await options.Events.Challenge(context);

        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        using var body = ReadBody(httpContext);
        body.RootElement.GetProperty("traceId").GetString().Should().Be("trace-challenge-known");
    }

    // [AER-014] 已登入但不具 Admin 角色。
    [Fact]
    public async Task DeactivateMember_ByNonAdmin_Returns403ProblemDetails()
    {
        var (client, _) = await OrderDisplayTestData.CreateBuyerClientAsync(_factory);

        var response = await client.PostAsync($"/api/admin/members/{Guid.NewGuid()}/deactivate", null);

        await AssertForbiddenProblemAsync(response);
    }

    // [AER-015] Admin 但 token 不帶合法 OrganizerId。
    [Fact]
    public async Task PostAdminEvents_WithoutOrganizerContext_Returns403ProblemDetails()
    {
        var client = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);

        var response = await client.PostAsJsonAsync(
            "/api/admin/events", new CreateEventRequest("t", DateTime.UtcNow.AddDays(30), Guid.NewGuid(), Guid.NewGuid()));

        await AssertForbiddenProblemAsync(response);
    }

    // [AER-017] 403 分支同樣取 HttpContext.TraceIdentifier。
    [Fact]
    public async Task OnForbidden_WithKnownTraceIdentifier_WritesThatTraceId()
    {
        var options = GetJwtBearerOptions();
        var httpContext = CreateHttpContext("trace-forbidden-known");
        var context = new ForbiddenContext(httpContext, CreateScheme(), options);

        await options.Events.Forbidden(context);

        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        using var body = ReadBody(httpContext);
        body.RootElement.GetProperty("traceId").GetString().Should().Be("trace-forbidden-known");
    }

    // [AER-018] 業務失敗轉換的 401 仍帶原 detail。
    [Fact]
    public async Task Login_WithWrongPassword_StillReturns401WithDetail()
    {
        var client = _factory.CreateClient();
        var email = AuthTestHelper.NewEmail();
        await AuthTestHelper.RegisterAsync(client, email);

        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest(email, "WrongPassword1", FakeCaptchaService.ValidToken, FakeCaptchaService.ValidAnswer));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.TryGetProperty("detail", out var detail).Should().BeTrue();
        detail.GetString().Should().NotBeNullOrWhiteSpace();
        body.RootElement.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
    }

    // [AER-018] 業務失敗轉換的 403（非訂單本人）仍帶原 detail。
    [Fact]
    public async Task ConfirmOrder_ByNonBuyer_StillReturns403WithDetail()
    {
        var organizerClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(_factory);
        var seededEvent = await OrderDisplayTestData.SeedEventAsync(_factory, organizerClient, "Challenge Business 403", seatCount: 1, countTicketTypeCount: 0);
        var (buyerClient, _) = await OrderDisplayTestData.CreateBuyerClientAsync(_factory);
        var (otherClient, _) = await OrderDisplayTestData.CreateBuyerClientAsync(_factory);
        var orderId = await OrderDisplayTestData.PlaceOrderAsync(buyerClient, seededEvent, seatItemCount: 1, countItemCount: 0);

        var response = await otherClient.PostAsync($"/api/orders/{orderId}/confirm", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.TryGetProperty("detail", out var detail).Should().BeTrue();
        detail.GetString().Should().NotBeNullOrWhiteSpace();
    }

    private string? CreateToken(string tokenCase) => tokenCase switch
    {
        "none" => null,
        "malformed" => "not-a-jwt",
        "wrongSignature" => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            "ProjectC.Tests",
            "ProjectC.Tests.Client",
            null,
            DateTime.UtcNow.AddMinutes(-30),
            new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes("a-completely-different-signing-key-32+chars")),
                SecurityAlgorithms.HmacSha256))),
        "expired" => SignedAccessTokenFactory.CreateSignedAccessToken(
            _factory.Services, Guid.NewGuid(), "expired@example.com", null, FixedExpiredAtUtc),
        _ => throw new ArgumentOutOfRangeException(nameof(tokenCase)),
    };

    private JwtBearerOptions GetJwtBearerOptions() =>
        _factory.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);

    private static AuthenticationScheme CreateScheme() =>
        new(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler));

    private DefaultHttpContext CreateHttpContext(string traceIdentifier) => new()
    {
        TraceIdentifier = traceIdentifier,
        RequestServices = _factory.Services,
        Response = { Body = new MemoryStream() },
    };

    private static JsonDocument ReadBody(HttpContext httpContext)
    {
        httpContext.Response.Body.Position = 0;
        return JsonDocument.Parse(httpContext.Response.Body);
    }

    private static async Task AssertForbiddenProblemAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType!.MediaType.Should().Be(ProblemJsonContentType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertProblemBody(body, 403, "Forbidden");
    }

    private static void AssertProblemBody(JsonDocument body, int status, string title)
    {
        body.RootElement.GetProperty("status").GetInt32().Should().Be(status);
        body.RootElement.GetProperty("title").GetString().Should().Be(title);
        body.RootElement.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
        body.RootElement.TryGetProperty("detail", out _).Should().BeFalse();
    }
}

public class JwtForbidProblemDetailsTests : IClassFixture<ForbidEndpointWebApplicationFactory>
{
    private readonly ForbidEndpointWebApplicationFactory _factory;

    public JwtForbidProblemDetailsTests(ForbidEndpointWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // [AER-016] 控制器 Forbid() 與 Policy 失敗同格式。
    [Fact]
    public async Task ForbidTestEndpoint_WhenControllerCallsForbid_Returns403ProblemDetails()
    {
        var (client, _) = await OrderDisplayTestData.CreateBuyerClientAsync(_factory);

        var response = await client.GetAsync("/api/test-support/forbid");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("status").GetInt32().Should().Be(403);
        body.RootElement.GetProperty("title").GetString().Should().Be("Forbidden");
        body.RootElement.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
        body.RootElement.TryGetProperty("detail", out _).Should().BeFalse();
    }
}
