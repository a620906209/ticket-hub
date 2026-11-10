using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Application.Authentication.Login;
using ProjectC.Application.Members.Register;
using ProjectC.Application.PurchaseQueue.JoinPurchaseQueue;
using ProjectC.Infrastructure.Persistence;
using ProjectC.WebApi.Tests.TestSupport;
using StackExchange.Redis;

namespace ProjectC.WebApi.Tests.Captcha;

// captcha-test-mode CAPTCHA-MODE-003／004／005：AcceptAny（非 Production）下，核對視為通過，
// 其餘行為（產生端點、欄位驗證、限流）不變。每個 AcceptAny 案例都附同一組請求在 Enforced 下的對照，
// 證明差異來自模式而不是請求本身。
public class CaptchaAcceptAnyModeTests : IClassFixture<CaptchaModeTestWebApplicationFactory>
{
    private const string NeverGeneratedAnswer = "anything";

    private readonly CaptchaModeTestWebApplicationFactory _factory;

    public CaptchaAcceptAnyModeTests(CaptchaModeTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static string NewUnknownToken() => Guid.NewGuid().ToString();

    private static async Task AssertCaptchaInvalidAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("title").GetString().Should().Be("CaptchaInvalid");
    }

    // (a) login：正確帳密 + 未曾產生的 token／任意答案 → 同一次回應 200。
    [Fact]
    public async Task Login_WithNeverGeneratedTokenAndAnyAnswer_Returns200UnderAcceptAnyButCaptchaInvalidUnderEnforced()
    {
        var email = AuthTestHelper.NewEmail();
        await AuthTestHelper.RegisterAsync(_factory.WithFakeCaptcha().CreateClient(), email);
        var request = new LoginRequest(email, AuthTestHelper.DefaultPassword, NewUnknownToken(), NeverGeneratedAnswer);

        var acceptAnyResponse = await _factory.WithMode("AcceptAny").CreateClient().PostAsJsonAsync("/api/auth/login", request);
        var enforcedResponse = await _factory.WithMode("Enforced").CreateClient().PostAsJsonAsync("/api/auth/login", request);

        acceptAnyResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertCaptchaInvalidAsync(enforcedResponse);
    }

    // (a) register：有效欄位 → 同一次回應 201，且 DB 可查到該會員。
    [Fact]
    public async Task Register_WithNeverGeneratedTokenAndAnyAnswer_Returns201AndCreatesMemberUnderAcceptAnyButCaptchaInvalidUnderEnforced()
    {
        var email = AuthTestHelper.NewEmail();
        var request = new RegisterMemberRequest(email, AuthTestHelper.DefaultPassword, "Alice", NewUnknownToken(), NeverGeneratedAnswer);
        var acceptAnyFactory = _factory.WithMode("AcceptAny");

        var enforcedResponse = await _factory.WithMode("Enforced").CreateClient().PostAsJsonAsync("/api/auth/register", request);
        var acceptAnyResponse = await acceptAnyFactory.CreateClient().PostAsJsonAsync("/api/auth/register", request);

        await AssertCaptchaInvalidAsync(enforcedResponse);
        acceptAnyResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        using var scope = acceptAnyFactory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await dbContext.Members.AsNoTracking().CountAsync(m => m.Email == email)).Should().Be(1);
    }

    // (a) join purchase queue：有效會員、已開啟佇列模式的活動 → 同一次回應 201，且 DB 有排隊項目。
    [Fact]
    public async Task JoinPurchaseQueue_WithNeverGeneratedTokenAndAnyAnswer_Returns201AndCreatesEntryUnderAcceptAnyButCaptchaInvalidUnderEnforced()
    {
        var setupFactory = _factory.WithFakeCaptcha();
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(setupFactory);
        var eventId = await CaptchaModeTestData.SeedQueueModeEnabledEventAsync(adminClient);
        var memberClient = await CaptchaModeTestData.CreateAuthenticatedMemberClientAsync(setupFactory, AuthTestHelper.NewEmail());
        var request = new JoinPurchaseQueueRequest(NewUnknownToken(), NeverGeneratedAnswer);
        var acceptAnyFactory = _factory.WithMode("AcceptAny");

        var enforcedClient = _factory.WithMode("Enforced").CreateClient();
        enforcedClient.DefaultRequestHeaders.Authorization = memberClient.DefaultRequestHeaders.Authorization;
        var enforcedResponse = await enforcedClient.PostAsJsonAsync($"/api/events/{eventId}/queue/entries", request);
        var acceptAnyClient = acceptAnyFactory.CreateClient();
        acceptAnyClient.DefaultRequestHeaders.Authorization = memberClient.DefaultRequestHeaders.Authorization;
        var acceptAnyResponse = await acceptAnyClient.PostAsJsonAsync($"/api/events/{eventId}/queue/entries", request);

        await AssertCaptchaInvalidAsync(enforcedResponse);
        acceptAnyResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        using var scope = acceptAnyFactory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await dbContext.PurchaseQueueEntries.AsNoTracking().CountAsync(e => e.EventId == eventId)).Should().Be(1);
    }

    // (b) CAPTCHA-MODE-004：產生端點不變，Redis 寫入帶 TTL 的雜湊。
    [Fact]
    public async Task GetCaptcha_UnderAcceptAny_ReturnsTokenAndImageAndWritesKeyWithTtl()
    {
        var acceptAnyFactory = _factory.WithMode("AcceptAny");

        var response = await acceptAnyFactory.CreateClient().GetAsync("/api/captcha");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var token = document.RootElement.GetProperty("token").GetString();
        var imageBase64 = document.RootElement.GetProperty("imageBase64").GetString();
        token.Should().NotBeNullOrWhiteSpace();
        Convert.FromBase64String(imageBase64!).Should().NotBeEmpty();

        var database = acceptAnyFactory.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        var ttl = await database.KeyTimeToLiveAsync($"captcha:{token}");
        ttl.Should().NotBeNull("Redis 必須有對應 key 且帶 TTL");
        ttl!.Value.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(120));
    }

    // (c) CAPTCHA-MODE-005：欄位驗證不被繞過。
    public static TheoryData<string, string, string> InvalidCaptchaFields => new()
    {
        { "", "ABCD", "captchaToken" },
        { "   ", "ABCD", "captchaToken" },
        { new string('t', 65), "ABCD", "captchaToken" },
        { "valid-token", "", "captchaAnswer" },
        { "valid-token", "   ", "captchaAnswer" },
        { "valid-token", new string('a', 17), "captchaAnswer" },
    };

    [Theory]
    [MemberData(nameof(InvalidCaptchaFields))]
    public async Task Login_UnderAcceptAnyWithInvalidCaptchaFields_Returns400ValidationWithFieldErrors(string token, string answer, string expectedField)
    {
        var response = await _factory.WithMode("AcceptAny").CreateClient().PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(AuthTestHelper.NewEmail(), AuthTestHelper.DefaultPassword, token, answer));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("title").GetString().Should().Be("Validation");
        document.RootElement.GetProperty("errors").EnumerateObject().Select(p => p.Name)
            .Should().Contain(name => string.Equals(name, expectedField, StringComparison.OrdinalIgnoreCase));
    }

    // (d) CAPTCHA-MODE-005：產生端點限流不被繞過（同一來源 IP 前 N 次 200、第 N+1 次 429）。
    [Fact]
    public async Task GetCaptcha_UnderAcceptAnyBeyondPermitLimit_Returns429()
    {
        const int permitLimit = 3;
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Captcha:VerificationMode", "AcceptAny");
            builder.ConfigureAppConfiguration((_, configBuilder) => configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CaptchaRateLimiting:PermitLimit"] = permitLimit.ToString(),
                ["CaptchaRateLimiting:WindowSeconds"] = "60",
            }));
        });
        var client = factory.CreateClient();

        for (var i = 0; i < permitLimit; i++)
        {
            (await client.GetAsync("/api/captcha")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await client.GetAsync("/api/captcha")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }
}
