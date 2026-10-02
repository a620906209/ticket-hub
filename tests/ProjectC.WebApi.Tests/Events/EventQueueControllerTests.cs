using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Application.Common;
using ProjectC.Application.Events.CreateEvent;
using ProjectC.Application.Members;
using ProjectC.Application.PurchaseQueue.GetMyQueueStatus;
using ProjectC.Application.PurchaseQueue.JoinPurchaseQueue;
using ProjectC.Application.Venues.CreateSeatMap;
using ProjectC.Application.Venues.CreateVenue;
using ProjectC.Domain.Members;
using ProjectC.Domain.PurchaseQueue;
using ProjectC.Infrastructure.Persistence;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Events;

// PQ-JOIN-007~009（design.md 決策 7；purchase-queue spec）——這幾個 Scenario 涉及 [Authorize] 中介軟體
// 層級的角色/未登入判斷與 HTTP request body 的實際繫結行為，MUST 走真正的 HTTP 呼叫驗證，
// 不能只在 JoinPurchaseQueueHandler 層級測（見 tasks.md 12.4b）。
public class EventQueueControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public EventQueueControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<Guid> ReadCreatedIdAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreatedResponse>();
        return created!.Id;
    }

    private async Task<Guid> SeedQueueModeEnabledEventAsync(HttpClient adminClient)
    {
        var venueResponse = await adminClient.PostAsJsonAsync("/api/admin/venues", new CreateVenueRequest("Queue Test Venue"));
        var venueId = await ReadCreatedIdAsync(venueResponse);
        var seatMapResponse = await adminClient.PostAsJsonAsync(
            $"/api/admin/venues/{venueId}/seat-maps", new CreateSeatMapRequest([new SeatRequest("A", "1")]));
        var seatMapId = await ReadCreatedIdAsync(seatMapResponse);
        var eventResponse = await adminClient.PostAsJsonAsync(
            "/api/admin/events", new CreateEventRequest("Queue Test Event", DateTime.UtcNow.AddDays(30), venueId, seatMapId));
        var eventId = await ReadCreatedIdAsync(eventResponse);

        var patchResponse = await adminClient.PatchAsJsonAsync($"/api/admin/events/{eventId}/queue-mode", new { enabled = true });
        patchResponse.EnsureSuccessStatusCode();

        return eventId;
    }

    private async Task<HttpClient> CreateAuthenticatedMemberClientAsync(string? email = null)
    {
        var client = _factory.CreateClient();
        var tokens = await AuthTestHelper.RegisterAndLoginAsync(client, email ?? AuthTestHelper.NewEmail());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    private async Task<Guid> ReadOwnMemberIdAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/members/me");
        var profile = await response.Content.ReadFromJsonAsync<MemberProfileDto>();
        return profile!.Id;
    }

    [Fact]
    public async Task JoinQueue_AsAdminRole_Returns201AndCreatesEntry()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(_factory);
        var eventId = await SeedQueueModeEnabledEventAsync(adminClient);

        var response = await adminClient.PostAsJsonAsync(
            $"/api/events/{eventId}/queue/entries",
            new JoinPurchaseQueueRequest(FakeCaptchaService.ValidToken, FakeCaptchaService.ValidAnswer));

        response.StatusCode.Should().Be(HttpStatusCode.Created, "Admin 角色帳號應依一般會員的既定規則處理，不因角色而被拒絕");
    }

    [Fact]
    public async Task JoinQueue_WithoutAuthentication_Returns401()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(_factory);
        var eventId = await SeedQueueModeEnabledEventAsync(adminClient);
        var anonymousClient = _factory.CreateClient();

        var response = await anonymousClient.PostAsync($"/api/events/{eventId}/queue/entries", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task JoinQueue_WhenRequestBodyCarriesAnotherMemberId_IgnoresItAndUsesCallersOwnJwtIdentity()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(_factory);
        var eventId = await SeedQueueModeEnabledEventAsync(adminClient);

        var otherClient = await CreateAuthenticatedMemberClientAsync();
        var otherMemberId = await ReadOwnMemberIdAsync(otherClient);

        var callerClient = await CreateAuthenticatedMemberClientAsync();
        var callerMemberId = await ReadOwnMemberIdAsync(callerClient);

        // JoinPurchaseQueueRequest 只宣告 CaptchaToken／CaptchaAnswer 兩個欄位（不接受 memberId），
        // 這裡刻意夾帶一個看似合法的 memberId 欄位，確認 model binding 會忽略它，不會被拿來覆寫
        // 排隊紀錄的會員身份。
        var response = await callerClient.PostAsJsonAsync(
            $"/api/events/{eventId}/queue/entries",
            new { memberId = otherMemberId, captchaToken = FakeCaptchaService.ValidToken, captchaAnswer = FakeCaptchaService.ValidAnswer });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var entryId = (await response.Content.ReadFromJsonAsync<CreatedResponse>())!.Id;
        var entry = await dbContext.PurchaseQueueEntries.AsNoTracking().SingleAsync(e => e.Id == entryId);
        entry.MemberId.Should().Be(callerMemberId);
        entry.MemberId.Should().NotBe(otherMemberId);
    }

    // CAPTCHA-QUEUE-003：查詢排隊狀態端點（輪詢用，非建立操作）不需要驗證碼欄位仍可正常回應
    // （captcha-verification design.md Non-Goals：不涉及查詢排隊狀態端點，加驗證碼會破壞既有輪詢體驗）。
    [Fact]
    public async Task GetMyQueueStatus_WithoutAnyCaptchaField_StillRespondsNormally()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(_factory);
        var eventId = await SeedQueueModeEnabledEventAsync(adminClient);
        var memberClient = await CreateAuthenticatedMemberClientAsync();

        var response = await memberClient.GetAsync($"/api/events/{eventId}/queue/entries/me");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // factory 內真實的 PurchaseQueueAdmissionService 會定期推進；先以 Admitted 紀錄佔滿入場名額，
    // 確保之後加入的會員穩定停在 Waiting，不會在斷言之間被背景服務改成 Admitted（否則測試會間歇失敗）。
    private async Task FillAdmissionSlotsAsync(Guid eventId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var maxConcurrentAdmittedBuyers = scope.ServiceProvider.GetRequiredService<PurchaseQueueOptions>().MaxConcurrentAdmittedBuyers;
        var now = DateTime.UtcNow;
        for (var i = 0; i < maxConcurrentAdmittedBuyers; i++)
        {
            var member = Member.Register($"slot-holder-{Guid.NewGuid():N}@example.com", "Slot Holder", "hash");
            var entry = new PurchaseQueueEntry(Guid.NewGuid(), eventId, member.Id, now.AddMinutes(-60));
            entry.Admit(now, now.AddMinutes(30));
            dbContext.Members.Add(member);
            dbContext.PurchaseQueueEntries.Add(entry);
        }

        await dbContext.SaveChangesAsync();
    }

    private async Task<PurchaseQueueEntry> ReadQueueEntryAsync(Guid entryId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await dbContext.PurchaseQueueEntries.AsNoTracking().SingleAsync(e => e.Id == entryId);
    }

    // [PQ-STATUS-008] HTTP + 真實 DB：活動所屬 Organizer 關閉熱門搶購模式後，查詢回應 queueModeEnabled == false，
    // 既有 Waiting 紀錄如實回傳且未被清理或改寫（purchase-queue-organizer-scoping tasks.md 4.12b）。
    [Fact]
    public async Task GetMyQueueStatus_AfterOwningOrganizerDisablesQueueMode_ReturnsQueueModeDisabledAndLeavesEntryUntouched()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var eventId = await SeedQueueModeEnabledEventAsync(organizerClient);
        await FillAdmissionSlotsAsync(eventId);
        var memberClient = await CreateAuthenticatedMemberClientAsync();
        var joinResponse = await memberClient.PostAsJsonAsync(
            $"/api/events/{eventId}/queue/entries",
            new JoinPurchaseQueueRequest(FakeCaptchaService.ValidToken, FakeCaptchaService.ValidAnswer));
        joinResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var entryId = (await joinResponse.Content.ReadFromJsonAsync<CreatedResponse>())!.Id;
        var baseline = await ReadQueueEntryAsync(entryId);
        baseline.Status.Should().Be(PurchaseQueueEntryStatus.Waiting);

        var disableResponse = await organizerClient.PatchAsJsonAsync($"/api/admin/events/{eventId}/queue-mode", new { enabled = false });
        disableResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var statusResponse = await memberClient.GetAsync($"/api/events/{eventId}/queue/entries/me");

        statusResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var status = (await statusResponse.Content.ReadFromJsonAsync<QueueStatusDto>())!;
        status.QueueModeEnabled.Should().BeFalse();
        status.Status.Should().Be("Waiting");
        status.WaitingCount.Should().Be(0, "佔位紀錄皆為 Admitted，前方沒有 Waiting 紀錄");
        var afterDisable = await ReadQueueEntryAsync(entryId);
        afterDisable.Status.Should().Be(baseline.Status);
        afterDisable.JoinedAtUtc.Should().Be(baseline.JoinedAtUtc);
        afterDisable.AdmittedAtUtc.Should().Be(baseline.AdmittedAtUtc);
        afterDisable.AdmissionExpiresAtUtc.Should().Be(baseline.AdmissionExpiresAtUtc);
    }

    // ---- real-name-verification：加入排隊的實名閘門（PQ-RN-JOIN-001~006） ----

    private async Task<int> CountQueueEntriesAsync(Guid eventId, Guid memberId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await dbContext.PurchaseQueueEntries.AsNoTracking().CountAsync(e => e.EventId == eventId && e.MemberId == memberId);
    }

    // [PQ-RN-JOIN-001] 未登記者不得佔用排隊名額（否則取得入場資格後才在下單被擋，名額白白浪費）。
    [Fact]
    public async Task JoinQueue_WhenEventRequiresRealNameAndMemberUnregistered_Returns403WithoutCreatingEntry()
    {
        var organizerClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(_factory);
        var seededEvent = await RealNameTestData.SeedEventAsync(_factory, organizerClient, isRealNameRequired: true, isQueueModeEnabled: true);
        var member = await RealNameTestData.CreateMemberAsync(_factory);

        var response = await RealNameTestData.JoinQueueAsync(member.Client, seededEvent.EventId);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await RealNameTestData.ReadProblemTitleAsync(response)).Should().Be("RealNameRequired");
        (await CountQueueEntriesAsync(seededEvent.EventId, member.MemberId)).Should().Be(0);
    }

    // [PQ-RN-JOIN-002]
    [Fact]
    public async Task JoinQueue_WhenEventRequiresRealNameAndMemberRegistered_Returns201WithWaitingEntry()
    {
        var organizerClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(_factory);
        var seededEvent = await RealNameTestData.SeedEventAsync(_factory, organizerClient, isRealNameRequired: true, isQueueModeEnabled: true);
        await FillAdmissionSlotsAsync(seededEvent.EventId);
        var member = await RealNameTestData.CreateRegisteredMemberAsync(_factory);

        var response = await RealNameTestData.JoinQueueAsync(member.Client, seededEvent.EventId);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var entry = await ReadQueueEntryAsync((await response.Content.ReadFromJsonAsync<CreatedResponse>())!.Id);
        entry.MemberId.Should().Be(member.MemberId);
        entry.Status.Should().Be(PurchaseQueueEntryStatus.Waiting);
    }

    // [PQ-RN-JOIN-003] 驗證碼檢查先於實名閘門：機器人流量不得藉由 403／201 的差異探測會員是否已登記實名。
    [Fact]
    public async Task JoinQueue_WhenCaptchaWrongOnRealNameEvent_ReturnsCaptchaInvalidNotRealNameRequired()
    {
        var organizerClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(_factory);
        var seededEvent = await RealNameTestData.SeedEventAsync(_factory, organizerClient, isRealNameRequired: true, isQueueModeEnabled: true);
        var member = await RealNameTestData.CreateMemberAsync(_factory);

        var response = await member.Client.PostAsJsonAsync(
            $"/api/events/{seededEvent.EventId}/queue/entries",
            new JoinPurchaseQueueRequest(FakeCaptchaService.ValidToken, "WRONG"));

        (await RealNameTestData.ReadProblemTitleAsync(response)).Should().Be(nameof(ErrorType.CaptchaInvalid));
        (await CountQueueEntriesAsync(seededEvent.EventId, member.MemberId)).Should().Be(0);
    }

    // [PQ-RN-JOIN-004]
    [Fact]
    public async Task JoinQueue_WhenEventDoesNotRequireRealNameAndMemberUnregistered_Returns201()
    {
        var organizerClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(_factory);
        var seededEvent = await RealNameTestData.SeedEventAsync(_factory, organizerClient, isRealNameRequired: false, isQueueModeEnabled: true);
        var member = await RealNameTestData.CreateMemberAsync(_factory);

        var response = await RealNameTestData.JoinQueueAsync(member.Client, seededEvent.EventId);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await CountQueueEntriesAsync(seededEvent.EventId, member.MemberId)).Should().Be(1);
    }

    // [PQ-RN-JOIN-005]
    [Fact]
    public async Task JoinQueue_WhenEventDoesNotExistAndMemberUnregistered_Returns404NotRealNameRequired()
    {
        var member = await RealNameTestData.CreateMemberAsync(_factory);

        var response = await RealNameTestData.JoinQueueAsync(member.Client, Guid.NewGuid());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await RealNameTestData.ReadProblemTitleAsync(response)).Should().NotBe("RealNameRequired");
    }

    // [PQ-RN-JOIN-006] 未開熱門搶購模式時沿用既有 409，不因實名設定改變錯誤種類。
    [Fact]
    public async Task JoinQueue_WhenRealNameEventHasQueueModeDisabled_Returns409NotRealNameRequired()
    {
        var organizerClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(_factory);
        var seededEvent = await RealNameTestData.SeedEventAsync(_factory, organizerClient, isRealNameRequired: true, isQueueModeEnabled: false);
        var member = await RealNameTestData.CreateMemberAsync(_factory);

        var response = await RealNameTestData.JoinQueueAsync(member.Client, seededEvent.EventId);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await RealNameTestData.ReadProblemTitleAsync(response)).Should().NotBe("RealNameRequired");
        (await CountQueueEntriesAsync(seededEvent.EventId, member.MemberId)).Should().Be(0);
    }
}
