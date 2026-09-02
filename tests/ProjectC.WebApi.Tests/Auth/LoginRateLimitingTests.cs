using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using ProjectC.Application.Authentication.Login;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Auth;

// api-rate-limiting spec LRL-001~003、005、010。每個測試方法各自 new 一個全新的
// LoginRateLimitedWebApplicationFactory 實例（不透過 IClassFixture 共用），確保每個測試方法擁有獨立的
// TestServer、獨立的 in-memory rate limiter 狀態，不受其他測試方法執行順序或殘留額度影響——只共用
// LoginRateLimitTestDatabaseFixture 這個純資料庫容器資源（不含限流器狀態），避免每個測試方法都重新
// 啟動一次 Testcontainers 容器（login-rate-limiting design.md 決策 6）。
public class LoginRateLimitingTests : IClassFixture<LoginRateLimitTestDatabaseFixture>
{
    private readonly LoginRateLimitTestDatabaseFixture _databaseFixture;

    public LoginRateLimitingTests(LoginRateLimitTestDatabaseFixture databaseFixture)
    {
        _databaseFixture = databaseFixture;
    }

    private LoginRateLimitedWebApplicationFactory CreateFactory()
        => new(_databaseFixture.ConnectionString);

    private static Task<HttpResponseMessage> AttemptLoginWithFakeCredentialsAsync(HttpClient client)
        => client.PostAsJsonAsync("/api/auth/login", new LoginRequest(AuthTestHelper.NewEmail(), "WrongPassword1"));

    /// <summary>平行送出 N 次請求，而非循序 await——Fixed Window 限流器的額度計算不受到達順序影響，
    /// 但循序 await 讓「耗盡額度所需的實際耗時」等於 N 次請求延遲的總和，在系統負載較高、
    /// 單次請求延遲增加時，可能讓耗盡額度的過程本身就超過視窗秒數，導致視窗提前重置、
    /// 測試預期的限流沒有觸發（實測發現：多個 Testcontainers 整合測試套件同時跑時會重現這個
    /// flaky 失敗）。平行送出讓耗時趨近於單次請求延遲，而非隨 N 累加，從根因消除這個問題，
    /// 不需要放寬視窗秒數（避免連帶拖慢依賴 Task.Delay(WindowSeconds) 的既有測試）。</summary>
    private static Task<HttpResponseMessage[]> AttemptLoginsWithFakeCredentialsAsync(HttpClient client, int count)
        => Task.WhenAll(Enumerable.Range(0, count).Select(_ => AttemptLoginWithFakeCredentialsAsync(client)));

    [Fact]
    public async Task Login_WithRequestsUnderTheLimit_AllSucceedWithoutBeingRateLimited()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var responses = await AttemptLoginsWithFakeCredentialsAsync(client, LoginRateLimitedWebApplicationFactory.LoginPermitLimit - 1);

        responses.Should().OnlyContain(response => response.StatusCode != HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Login_WithExactlyThePermitLimitRequests_AllSucceedWithoutBeingRateLimited()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var responses = await AttemptLoginsWithFakeCredentialsAsync(client, LoginRateLimitedWebApplicationFactory.LoginPermitLimit);

        responses.Should().OnlyContain(response => response.StatusCode != HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Login_AfterTheWindowResets_RequestsAreAllowedAgain()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        await AttemptLoginsWithFakeCredentialsAsync(client, LoginRateLimitedWebApplicationFactory.LoginPermitLimit);

        var exhaustedResponse = await AttemptLoginWithFakeCredentialsAsync(client);
        exhaustedResponse.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "時間窗重置前置條件：額度應已耗盡");

        await Task.Delay(TimeSpan.FromSeconds(LoginRateLimitedWebApplicationFactory.LoginWindowSeconds + 1));

        var response = await AttemptLoginWithFakeCredentialsAsync(client);
        response.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, "時間窗重置後應該恢復可請求");
    }

    // LRL-003：超額請求即使帳密正確也一律拒絕、不核發 Token。驗證的是可觀察的 HTTP 結果，不透過 mock
    // 驗證 LoginHandler 內部未被呼叫——middleware 短路管線是框架保證，與既有下單限流依賴同一個保證
    // （見 login-rate-limiting design.md 決策 6 第 5 點、spec.md Requirement 措辭）。
    [Fact]
    public async Task Login_WithCorrectCredentialsAsTheRequestOverTheLimit_IsStillRejectedWithoutIssuingTokens()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var email = AuthTestHelper.NewEmail();
        await AuthTestHelper.RegisterAsync(client, email);

        await Task.WhenAll(Enumerable.Range(0, LoginRateLimitedWebApplicationFactory.LoginPermitLimit)
            .Select(_ => client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "WrongPassword1"))));

        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, AuthTestHelper.DefaultPassword));

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "即使帳密正確，超額請求也一律拒絕");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.TryGetProperty("accessToken", out _).Should().BeFalse("超額請求不得核發 Access Token");
        document.RootElement.TryGetProperty("refreshToken", out _).Should().BeFalse("超額請求不得核發 Refresh Token");
    }

    [Fact]
    public async Task Login_WhenRateLimited_ReturnsProblemDetailsWithRetryAfterHeader()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        await AttemptLoginsWithFakeCredentialsAsync(client, LoginRateLimitedWebApplicationFactory.LoginPermitLimit);

        var response = await AttemptLoginWithFakeCredentialsAsync(client);

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        response.Headers.Should().ContainKey("Retry-After");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("status").GetInt32().Should().Be(429);
        document.RootElement.GetProperty("title").GetString().Should().Be("TooManyRequests");
        document.RootElement.TryGetProperty("traceId", out _).Should().BeTrue();
    }
}
