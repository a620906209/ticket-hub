using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Application.PurchaseQueue.JoinPurchaseQueue;
using ProjectC.Infrastructure.Persistence;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Captcha;

// CAPTCHA-QUEUE-001／002（HTTP 端到端）：直接使用 CustomWebApplicationFactory（已預設把
// ICaptchaService 替換為 FakeCaptchaService，見該檔案），附帶已知的固定 token／答案送出加入排隊請求
// ——真正的雜湊、TTL、一次性語意已在 RedisCaptchaServiceTests 用真實 Redis 驗證，此處不重複驗證。
public class PurchaseQueueCaptchaTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public PurchaseQueueCaptchaTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task JoinQueue_WithCorrectCaptcha_Succeeds()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(_factory);
        var eventId = await CaptchaModeTestData.SeedQueueModeEnabledEventAsync(adminClient);
        var memberClient = await CaptchaModeTestData.CreateAuthenticatedMemberClientAsync(_factory, AuthTestHelper.NewEmail());

        var response = await memberClient.PostAsJsonAsync(
            $"/api/events/{eventId}/queue/entries",
            new JoinPurchaseQueueRequest(FakeCaptchaService.ValidToken, FakeCaptchaService.ValidAnswer));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // CAPTCHA-QUEUE-001：驗證碼答案錯誤時被拒絕、不建立任何排隊紀錄。Title MUST 為可區分的
    // "CaptchaInvalid"（而非泛用 "Validation"），前端據此判斷是否為驗證碼錯誤而不是任何 400。
    [Fact]
    public async Task JoinQueue_WithWrongCaptchaAnswer_Returns400WithCaptchaInvalidTitleAndDoesNotCreateEntry()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(_factory);
        var eventId = await CaptchaModeTestData.SeedQueueModeEnabledEventAsync(adminClient);
        var memberClient = await CaptchaModeTestData.CreateAuthenticatedMemberClientAsync(_factory, AuthTestHelper.NewEmail());

        var response = await memberClient.PostAsJsonAsync(
            $"/api/events/{eventId}/queue/entries",
            new JoinPurchaseQueueRequest(FakeCaptchaService.ValidToken, "WRONG"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var responseBody = await response.Content.ReadAsStringAsync();
        using (var document = JsonDocument.Parse(responseBody))
        {
            document.RootElement.GetProperty("title").GetString().Should().Be("CaptchaInvalid");
        }

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var entryCount = await dbContext.PurchaseQueueEntries.AsNoTracking().CountAsync(e => e.EventId == eventId);
        entryCount.Should().Be(0);
    }

    // CAPTCHA-QUEUE-002：缺漏 CaptchaAnswer 欄位時被拒絕。
    [Fact]
    public async Task JoinQueue_WithMissingCaptchaAnswer_Returns400()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(_factory);
        var eventId = await CaptchaModeTestData.SeedQueueModeEnabledEventAsync(adminClient);
        var memberClient = await CaptchaModeTestData.CreateAuthenticatedMemberClientAsync(_factory, AuthTestHelper.NewEmail());

        var response = await memberClient.PostAsJsonAsync(
            $"/api/events/{eventId}/queue/entries",
            new JoinPurchaseQueueRequest(FakeCaptchaService.ValidToken, string.Empty));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // PQ-JOIN-004（6.3a）：對同一活動、同一會員、尚無任何排隊紀錄，幾乎同時送出兩個完整 HTTP 請求
    // （各自帶正確驗證碼），驗證「驗證碼檢查＋Handler＋Controller」疊加後的並發加入排隊行為——既有
    // PurchaseQueueRepositoryConcurrencyTests 只測到 Repository 層，不經過驗證碼檢查，見 tasks.md 6.6。
    [Fact]
    public async Task JoinQueue_TwoConcurrentRequestsFromSameMember_ResultInExactlyOneEntry()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(_factory);
        var eventId = await CaptchaModeTestData.SeedQueueModeEnabledEventAsync(adminClient);
        var memberClient = await CaptchaModeTestData.CreateAuthenticatedMemberClientAsync(_factory, AuthTestHelper.NewEmail());

        Task<HttpResponseMessage> SendJoinRequest() => memberClient.PostAsJsonAsync(
            $"/api/events/{eventId}/queue/entries",
            new JoinPurchaseQueueRequest(FakeCaptchaService.ValidToken, FakeCaptchaService.ValidAnswer));

        var responses = await Task.WhenAll(SendJoinRequest(), SendJoinRequest());

        responses.Should().OnlyContain(r => r.IsSuccessStatusCode, "兩個請求皆應成功（Idempotent，回傳同一筆紀錄）");

        var ids = await Task.WhenAll(responses.Select(async r => (await r.Content.ReadFromJsonAsync<CreatedResponse>())!.Id));
        ids[0].Should().Be(ids[1], "兩次回應的 entry Id 應該相同");

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var entryCount = await dbContext.PurchaseQueueEntries.AsNoTracking().CountAsync(e => e.EventId == eventId);
        entryCount.Should().Be(1, "最終只應存在一筆進行中的排隊紀錄");
    }
}
