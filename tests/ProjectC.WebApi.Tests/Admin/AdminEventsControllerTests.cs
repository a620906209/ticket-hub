using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using ProjectC.Application.Events.CreateEvent;
using ProjectC.Application.Events.GetAdminEvents;
using ProjectC.Application.Events.GetEvents;
using ProjectC.Application.Events.GetEventSeats;
using ProjectC.Application.Members;
using ProjectC.Application.Orders.PlaceOrder;
using ProjectC.Application.Tickets.CreateTicketType;
using ProjectC.Application.Tickets.GetTicketTypes;
using ProjectC.Application.Venues.CreateSeatMap;
using ProjectC.Application.Venues.CreateVenue;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Admin;

public class AdminEventsControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AdminEventsControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<Guid> ReadCreatedIdAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreatedResponse>();
        return created!.Id;
    }

    private async Task<(Guid VenueId, Guid SeatMapId)> CreateVenueWithSeatMapAsync(HttpClient adminClient, string zoneCode = "A")
    {
        var venueResponse = await adminClient.PostAsJsonAsync("/api/admin/venues", new CreateVenueRequest("Test Venue"));
        var venueId = await ReadCreatedIdAsync(venueResponse);

        var seatMapResponse = await adminClient.PostAsJsonAsync(
            $"/api/admin/venues/{venueId}/seat-maps",
            new CreateSeatMapRequest([new SeatRequest(zoneCode, "1")]));
        var seatMapId = await ReadCreatedIdAsync(seatMapResponse);

        return (venueId, seatMapId);
    }

    private async Task<Guid> CreateEventAsync(HttpClient adminClient, Guid venueId, Guid seatMapId)
    {
        var response = await adminClient.PostAsJsonAsync(
            "/api/admin/events",
            new CreateEventRequest("Concert", DateTime.UtcNow.AddDays(30), venueId, seatMapId));
        return await ReadCreatedIdAsync(response);
    }

    [Fact]
    public async Task CreateEvent_WithValidVenueAndSeatMap_ReturnsCreated()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(organizerClient);

        var response = await organizerClient.PostAsJsonAsync(
            "/api/admin/events",
            new CreateEventRequest("Concert", DateTime.UtcNow.AddDays(30), venueId, seatMapId));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateEvent_WithBlankTitle_Returns400()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(organizerClient);

        var response = await organizerClient.PostAsJsonAsync(
            "/api/admin/events",
            new CreateEventRequest("  ", DateTime.UtcNow.AddDays(30), venueId, seatMapId));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateEvent_WithNonExistentVenueOrSeatMap_Returns404()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);

        var response = await organizerClient.PostAsJsonAsync(
            "/api/admin/events",
            new CreateEventRequest("Concert", DateTime.UtcNow.AddDays(30), Guid.NewGuid(), Guid.NewGuid()));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // [EVT-AUTHZ-002] 已登入但尚未切換 Organizer 的使用者（含單純 Admin 角色未切換）呼叫管理端點 MUST 403，不建立任何資源。
    [Fact]
    public async Task CreateEvent_AsAdminWithoutSwitchingOrganizer_Returns403AndDoesNotCreateEvent()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(organizerClient);
        const string title = "CreateEvent_AsAdminWithoutSwitchingOrganizer probe";

        var response = await adminClient.PostAsJsonAsync(
            "/api/admin/events",
            new CreateEventRequest(title, DateTime.UtcNow.AddDays(30), venueId, seatMapId));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var listResponse = await organizerClient.GetAsync("/api/admin/events");
        var events = await listResponse.Content.ReadFromJsonAsync<List<AdminEventSummaryDto>>();
        events.Should().NotContain(e => e.Title == title);
    }

    [Fact]
    public async Task CreateTicketType_WithExistingZoneAndValidPrice_ReturnsCreated()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(organizerClient, zoneCode: "A");
        var eventId = await CreateEventAsync(organizerClient, venueId, seatMapId);

        var response = await organizerClient.PostAsJsonAsync(
            $"/api/admin/events/{eventId}/ticket-types",
            new CreateTicketTypeRequest("A", 500m));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateTicketType_WithInvalidPrice_Returns400()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(organizerClient, zoneCode: "A");
        var eventId = await CreateEventAsync(organizerClient, venueId, seatMapId);

        var response = await organizerClient.PostAsJsonAsync(
            $"/api/admin/events/{eventId}/ticket-types",
            new CreateTicketTypeRequest("A", 0m));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateTicketType_WithZoneNotInSeatMap_Returns400()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(organizerClient, zoneCode: "A");
        var eventId = await CreateEventAsync(organizerClient, venueId, seatMapId);

        var response = await organizerClient.PostAsJsonAsync(
            $"/api/admin/events/{eventId}/ticket-types",
            new CreateTicketTypeRequest("B", 500m));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateTicketType_WithNonExistentEvent_Returns404()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);

        var response = await organizerClient.PostAsJsonAsync(
            $"/api/admin/events/{Guid.NewGuid()}/ticket-types",
            new CreateTicketTypeRequest("A", 500m));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // [EVT-TICKET-004] 活動屬於其他 Organizer 時視同不存在（IDOR 防護），不得建立任何票種，不得回傳 403。
    [Fact]
    public async Task CreateTicketType_ForEventBelongingToAnotherOrganizer_Returns404AndDoesNotCreateTicketType()
    {
        var (ownerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(ownerClient, zoneCode: "A");
        var eventId = await CreateEventAsync(ownerClient, venueId, seatMapId);
        var (otherOrganizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        // 對照組：活動所屬 Organizer 自己建立的票種，證明事後查詢確實看得到這個活動的票種，「查無」斷言才有證明力。
        var ownerResponse = await ownerClient.PostAsJsonAsync(
            $"/api/admin/events/{eventId}/ticket-types",
            new CreateTicketTypeRequest("A", 500m));
        ownerResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var response = await otherOrganizerClient.PostAsJsonAsync(
            $"/api/admin/events/{eventId}/ticket-types",
            new CreateTicketTypeRequest("A", 777m));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var ticketTypes = await _factory.CreateClient().GetFromJsonAsync<List<TicketTypeDto>>($"/api/events/{eventId}/ticket-types");
        ticketTypes.Should().ContainSingle().Which.Price.Should().Be(500m, "只應有擁有者建立的票種，跨 Organizer 的請求不得寫入");
    }

    [Fact]
    public async Task CreateTicketType_WithLegacyPayloadMissingRequiresSeat_TreatsAsRequiringSeatAndSucceeds()
    {
        // 外部審查第四輪抓到的阻斷問題：MUST 用匿名物件送出只有舊欄位的原始 JSON，
        // 用強型別 CreateTicketTypeRequest 物件建構測不出「欄位缺失」這個情境
        // （強型別物件永遠會序列化出 RequiresSeat 的預設值，不是真的缺欄位）。
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(organizerClient, zoneCode: "A");
        var eventId = await CreateEventAsync(organizerClient, venueId, seatMapId);

        var response = await organizerClient.PostAsJsonAsync(
            $"/api/admin/events/{eventId}/ticket-types",
            new { ZoneCode = "A", Price = 500m });

        response.StatusCode.Should().Be(HttpStatusCode.Created,
            "缺 RequiresSeat 欄位的舊格式請求 MUST 視為綁座位模式，依既有分區驗證規則成功建立");
    }

    // ---- 建立活動記錄建立者、所屬 Organizer（透過 GET /api/admin/events 查詢驗證，POST 的成功回應只有 { id }） ----

    [Fact]
    public async Task CreateEvent_ThenGetAdminEvents_RecordsCreatedByMemberId()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var myProfileResponse = await organizerClient.GetAsync("/api/members/me");
        var memberId = (await myProfileResponse.Content.ReadFromJsonAsync<MemberProfileDto>())!.Id;
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(organizerClient);
        var eventId = await CreateEventAsync(organizerClient, venueId, seatMapId);

        var response = await organizerClient.GetAsync("/api/admin/events");

        var events = await response.Content.ReadFromJsonAsync<List<AdminEventSummaryDto>>();
        events.Should().ContainSingle(e => e.Id == eventId && e.CreatedByMemberId == memberId);
    }

    // [EVT-LIST-001] 後台專用活動列表查詢僅回傳呼叫端目前 Organizer 名下的活動。
    [Fact]
    public async Task GetEvents_WithEventsFromAnotherOrganizer_ReturnsOnlyOwnOrganizerEvents()
    {
        var (ownerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(ownerClient);
        var ownEventId = await CreateEventAsync(ownerClient, venueId, seatMapId);
        var (otherOrganizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var (otherVenueId, otherSeatMapId) = await CreateVenueWithSeatMapAsync(otherOrganizerClient);
        await CreateEventAsync(otherOrganizerClient, otherVenueId, otherSeatMapId);

        var response = await ownerClient.GetAsync("/api/admin/events");

        var events = await response.Content.ReadFromJsonAsync<List<AdminEventSummaryDto>>();
        events.Should().ContainSingle(e => e.Id == ownEventId);
    }

    // ---- 查詢活動列表（Admin 專用端點）需要已切換至一個 Approved Organizer ----

    [Fact]
    public async Task GetEvents_AsApprovedOrganizerMember_Returns200()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);

        var response = await organizerClient.GetAsync("/api/admin/events");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // [EVT-AUTHZ-002] 已登入但尚未切換 Organizer 者（含單純 Admin 角色）呼叫管理端點 MUST 403。
    [Fact]
    public async Task GetEvents_AsAdminWithoutSwitchingOrganizer_Returns403()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);

        var response = await adminClient.GetAsync("/api/admin/events");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetEvents_AsNonAdminMember_Returns403()
    {
        var email = AuthTestHelper.NewEmail();
        var client = _factory.CreateClient();
        var tokens = await AuthTestHelper.RegisterAndLoginAsync(client, email);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync("/api/admin/events");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetEvents_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/admin/events");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- PATCH /api/admin/events/{id}/queue-mode（rate-limiting-queue design.md 決策 2／6，
    // purchase-queue spec PQ-ADMIN-001~007；PQ-ADMIN-004／006／007 同時驗證 tasks.md 12.10 的
    // SetEventQueueModeRequest.Enabled（bool?）model binding 行為） ----

    private static Task<HttpResponseMessage> PatchQueueModeAsync(HttpClient client, Guid eventId, object body)
        => client.PatchAsJsonAsync($"/api/admin/events/{eventId}/queue-mode", body);

    private async Task<bool> ReadIsQueueModeEnabledAsync(Guid eventId)
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/events");
        var events = await response.Content.ReadFromJsonAsync<List<EventDto>>();
        return events!.Single(e => e.Id == eventId).IsQueueModeEnabled;
    }

    [Fact]
    public async Task SetQueueMode_AsAdminWithEnabledTrue_Returns204AndEnablesQueueMode()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(organizerClient);
        var eventId = await CreateEventAsync(organizerClient, venueId, seatMapId);

        var response = await PatchQueueModeAsync(adminClient, eventId, new { enabled = true });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ReadIsQueueModeEnabledAsync(eventId)).Should().BeTrue();
    }

    [Fact]
    public async Task SetQueueMode_AsAdminWithEnabledFalseAfterEnabling_Returns204AndDisablesQueueMode()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(organizerClient);
        var eventId = await CreateEventAsync(organizerClient, venueId, seatMapId);
        await PatchQueueModeAsync(adminClient, eventId, new { enabled = true });

        var response = await PatchQueueModeAsync(adminClient, eventId, new { enabled = false });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ReadIsQueueModeEnabledAsync(eventId)).Should().BeFalse();
    }

    [Fact]
    public async Task SetQueueMode_AsNonAdminMember_Returns403AndDoesNotChangeState()
    {
        var memberClient = _factory.CreateClient();
        var tokens = await AuthTestHelper.RegisterAndLoginAsync(memberClient);
        memberClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(organizerClient);
        var eventId = await CreateEventAsync(organizerClient, venueId, seatMapId);

        var response = await PatchQueueModeAsync(memberClient, eventId, new { enabled = true });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadIsQueueModeEnabledAsync(eventId)).Should().BeFalse();
    }

    [Fact]
    public async Task SetQueueMode_WithoutAuthentication_Returns401AndDoesNotChangeState()
    {
        var anonymousClient = _factory.CreateClient();
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(organizerClient);
        var eventId = await CreateEventAsync(organizerClient, venueId, seatMapId);

        var response = await PatchQueueModeAsync(anonymousClient, eventId, new { enabled = true });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ReadIsQueueModeEnabledAsync(eventId)).Should().BeFalse();
    }

    [Fact]
    public async Task SetQueueMode_WithMissingEnabledField_Returns400AndDoesNotChangeState()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(organizerClient);
        var eventId = await CreateEventAsync(organizerClient, venueId, seatMapId);

        var response = await PatchQueueModeAsync(adminClient, eventId, new { });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "Enabled 為 bool?，完全缺漏欄位 MUST 繫結為 null 並被 NotNull() 攔截，不得誤判為明確關閉");
        (await ReadIsQueueModeEnabledAsync(eventId)).Should().BeFalse();
    }

    [Fact]
    public async Task SetQueueMode_ForNonExistentEvent_Returns404()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);

        var response = await PatchQueueModeAsync(adminClient, Guid.NewGuid(), new { enabled = true });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SetQueueMode_WithEnabledAsWrongJsonType_Returns400AndDoesNotChangeState()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(organizerClient);
        var eventId = await CreateEventAsync(organizerClient, venueId, seatMapId);

        var response = await PatchQueueModeAsync(adminClient, eventId, new { enabled = "false" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "enabled 為字串而非 boolean 時，model binding 階段就應該失敗");
        (await ReadIsQueueModeEnabledAsync(eventId)).Should().BeFalse();
    }

    // ---- GET /api/admin/events/{eventId}/sales-report（sales-report tasks.md 4.3） ----

    // [RPT-AUTHZ-001] 已切換至 Approved Organizer（非 Admin）即可查詢自己活動的銷售報表
    [Fact]
    public async Task GetSalesReport_AsApprovedOrganizerNonAdminOnOwnEvent_Returns200()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(organizerClient);
        var eventId = await CreateEventAsync(organizerClient, venueId, seatMapId);

        var response = await organizerClient.GetAsync($"/api/admin/events/{eventId}/sales-report");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // [RPT-AUTHZ-002] 一般 Member 與 Admin 角色未切換 Organizer 皆 403（Admin 不再能繞過 Organizer 切換）
    [Fact]
    public async Task GetSalesReport_WithoutOrganizerContext_Returns403ForMemberAndAdmin()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(organizerClient);
        var eventId = await CreateEventAsync(organizerClient, venueId, seatMapId);
        var memberClient = _factory.CreateClient();
        var tokens = await AuthTestHelper.RegisterAndLoginAsync(memberClient);
        memberClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);

        var memberResponse = await memberClient.GetAsync($"/api/admin/events/{eventId}/sales-report");
        var adminResponse = await adminClient.GetAsync($"/api/admin/events/{eventId}/sales-report");

        memberResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        adminResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // [RPT-AUTHZ-003]
    [Fact]
    public async Task GetSalesReport_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/admin/events/{Guid.NewGuid()}/sales-report");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetSalesReport_ForNonExistentEvent_Returns404()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);

        var response = await organizerClient.GetAsync($"/api/admin/events/{Guid.NewGuid()}/sales-report");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // [RPT-AUTHZ-004] 其他 Organizer 的活動視同不存在：狀態碼與 body 皆與真正不存在時相同（除 ID 外逐字相同）
    [Fact]
    public async Task GetSalesReport_ForOtherOrganizerEvent_Returns404WithSameBodyAsMissingEvent()
    {
        var (organizerAClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var (organizerBClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(organizerBClient);
        var eventB = await CreateEventAsync(organizerBClient, venueId, seatMapId);
        var missingEventId = Guid.NewGuid();

        var otherOrganizerResponse = await organizerAClient.GetAsync($"/api/admin/events/{eventB}/sales-report");
        var missingResponse = await organizerAClient.GetAsync($"/api/admin/events/{missingEventId}/sales-report");

        otherOrganizerResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        missingResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await NotFoundResponseBody.ReadNormalizedAsync(otherOrganizerResponse, eventB))
            .Should().Be(await NotFoundResponseBody.ReadNormalizedAsync(missingResponse, missingEventId));
    }

    // [RPT-AUTHZ-005] RequireOrganizerContext 不即時查表：停權前核發、未過期的 Access Token 在過期前仍可查詢。
    // 這是 design.md Decision 2 的既知有界延遲視窗；若此測試失敗，代表 Policy 行為改變，spec 必須同步修改。
    [Fact]
    public async Task GetSalesReport_WithTokenIssuedBeforeOrganizerSuspended_IsStillAcceptedUntilExpiry()
    {
        var (organizerClient, organizerId) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(organizerClient);
        var eventId = await CreateEventAsync(organizerClient, venueId, seatMapId);
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);

        var suspendResponse = await adminClient.PatchAsync($"/api/admin/organizers/{organizerId}/suspend", null);
        suspendResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var response = await organizerClient.GetAsync($"/api/admin/events/{eventId}/sales-report");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetSalesReport_WithPaidOrder_ReturnsCorrectlySerializedJsonBody()
    {
        // 補上 DTO → ASP.NET JSON serialization 的整合驗證（Application 層測試只驗證 C# 物件本身，
        // 沒有經過真正的 HTTP 序列化路徑；用 JsonDocument 直接檢查駝峰命名的欄位是否存在，
        // 比反序列化回同一個 C# 型別更能抓到「欄位名稱不是駝峰」這類問題，因為反序列化預設對
        // 屬性名稱大小寫不敏感，PascalCase 誤寫也會反序列化成功、測不出來）。
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(organizerClient, zoneCode: "A");
        var eventId = await CreateEventAsync(organizerClient, venueId, seatMapId);
        var ticketTypeResponse = await organizerClient.PostAsJsonAsync(
            $"/api/admin/events/{eventId}/ticket-types",
            new CreateTicketTypeRequest("A", 500m));
        var ticketTypeId = await ReadCreatedIdAsync(ticketTypeResponse);

        var publicClient = _factory.CreateClient();
        var seatsResponse = await publicClient.GetAsync($"/api/events/{eventId}/seats");
        var eventSeatId = (await seatsResponse.Content.ReadFromJsonAsync<List<EventSeatDto>>())!.Single(s => s.ZoneCode == "A").EventSeatId;

        var buyerClient = _factory.CreateClient();
        var buyerTokens = await AuthTestHelper.RegisterAndLoginAsync(buyerClient);
        buyerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", buyerTokens.AccessToken);
        var orderResponse = await buyerClient.PostAsJsonAsync(
            "/api/orders",
            new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeatId, ticketTypeId)]));
        var orderId = await ReadCreatedIdAsync(orderResponse);
        await buyerClient.PostAsync($"/api/orders/{orderId}/confirm", null);

        var response = await organizerClient.GetAsync($"/api/admin/events/{eventId}/sales-report");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        root.GetProperty("totalRevenue").GetDecimal().Should().Be(500m);
        root.GetProperty("totalTicketsSold").GetInt32().Should().Be(1);
        root.GetProperty("unclassifiedItemCount").GetInt32().Should().Be(0);
        root.GetProperty("unclassifiedTicketsSold").GetInt32().Should().Be(0);
        root.GetProperty("unclassifiedRevenue").GetDecimal().Should().Be(0m);
        var byTicketType = root.GetProperty("byTicketType");
        byTicketType.GetArrayLength().Should().Be(1);
        var detail = byTicketType[0];
        detail.GetProperty("ticketTypeId").GetGuid().Should().Be(ticketTypeId);
        detail.GetProperty("zoneCode").GetString().Should().Be("A");
        detail.GetProperty("requiresSeat").GetBoolean().Should().BeTrue();
        detail.GetProperty("quantitySold").GetInt32().Should().Be(1);
        detail.GetProperty("revenue").GetDecimal().Should().Be(500m);
    }
}
