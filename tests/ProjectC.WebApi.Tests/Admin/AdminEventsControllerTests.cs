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
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(adminClient);

        var response = await adminClient.PostAsJsonAsync(
            "/api/admin/events",
            new CreateEventRequest("Concert", DateTime.UtcNow.AddDays(30), venueId, seatMapId));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateEvent_WithBlankTitle_Returns400()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(adminClient);

        var response = await adminClient.PostAsJsonAsync(
            "/api/admin/events",
            new CreateEventRequest("  ", DateTime.UtcNow.AddDays(30), venueId, seatMapId));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateEvent_WithNonExistentVenueOrSeatMap_Returns404()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);

        var response = await adminClient.PostAsJsonAsync(
            "/api/admin/events",
            new CreateEventRequest("Concert", DateTime.UtcNow.AddDays(30), Guid.NewGuid(), Guid.NewGuid()));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateTicketType_WithExistingZoneAndValidPrice_ReturnsCreated()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(adminClient, zoneCode: "A");
        var eventId = await CreateEventAsync(adminClient, venueId, seatMapId);

        var response = await adminClient.PostAsJsonAsync(
            $"/api/admin/events/{eventId}/ticket-types",
            new CreateTicketTypeRequest("A", 500m));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateTicketType_WithInvalidPrice_Returns400()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(adminClient, zoneCode: "A");
        var eventId = await CreateEventAsync(adminClient, venueId, seatMapId);

        var response = await adminClient.PostAsJsonAsync(
            $"/api/admin/events/{eventId}/ticket-types",
            new CreateTicketTypeRequest("A", 0m));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateTicketType_WithZoneNotInSeatMap_Returns400()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(adminClient, zoneCode: "A");
        var eventId = await CreateEventAsync(adminClient, venueId, seatMapId);

        var response = await adminClient.PostAsJsonAsync(
            $"/api/admin/events/{eventId}/ticket-types",
            new CreateTicketTypeRequest("B", 500m));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateTicketType_WithNonExistentEvent_Returns404()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);

        var response = await adminClient.PostAsJsonAsync(
            $"/api/admin/events/{Guid.NewGuid()}/ticket-types",
            new CreateTicketTypeRequest("A", 500m));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateTicketType_WithLegacyPayloadMissingRequiresSeat_TreatsAsRequiringSeatAndSucceeds()
    {
        // 外部審查第四輪抓到的阻斷問題：MUST 用匿名物件送出只有舊欄位的原始 JSON，
        // 用強型別 CreateTicketTypeRequest 物件建構測不出「欄位缺失」這個情境
        // （強型別物件永遠會序列化出 RequiresSeat 的預設值，不是真的缺欄位）。
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(adminClient, zoneCode: "A");
        var eventId = await CreateEventAsync(adminClient, venueId, seatMapId);

        var response = await adminClient.PostAsJsonAsync(
            $"/api/admin/events/{eventId}/ticket-types",
            new { ZoneCode = "A", Price = 500m });

        response.StatusCode.Should().Be(HttpStatusCode.Created,
            "缺 RequiresSeat 欄位的舊格式請求 MUST 視為綁座位模式，依既有分區驗證規則成功建立");
    }

    // ---- 建立活動記錄建立者（透過 GET /api/admin/events 查詢驗證，POST 的成功回應只有 { id }） ----

    [Fact]
    public async Task CreateEvent_ThenGetAdminEvents_RecordsCreatedByMemberId()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var myProfileResponse = await adminClient.GetAsync("/api/members/me");
        var adminMemberId = (await myProfileResponse.Content.ReadFromJsonAsync<MemberProfileDto>())!.Id;
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(adminClient);
        var eventId = await CreateEventAsync(adminClient, venueId, seatMapId);

        var response = await adminClient.GetAsync("/api/admin/events");

        var events = await response.Content.ReadFromJsonAsync<List<AdminEventSummaryDto>>();
        events.Should().ContainSingle(e => e.Id == eventId && e.CreatedByMemberId == adminMemberId);
    }

    // ---- 查詢活動列表（Admin 專用端點）需要 Admin 角色 ----

    [Fact]
    public async Task GetEvents_AsAdmin_Returns200()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);

        var response = await adminClient.GetAsync("/api/admin/events");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
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
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(adminClient);
        var eventId = await CreateEventAsync(adminClient, venueId, seatMapId);

        var response = await PatchQueueModeAsync(adminClient, eventId, new { enabled = true });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ReadIsQueueModeEnabledAsync(eventId)).Should().BeTrue();
    }

    [Fact]
    public async Task SetQueueMode_AsAdminWithEnabledFalseAfterEnabling_Returns204AndDisablesQueueMode()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(adminClient);
        var eventId = await CreateEventAsync(adminClient, venueId, seatMapId);
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
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(adminClient);
        var eventId = await CreateEventAsync(adminClient, venueId, seatMapId);

        var response = await PatchQueueModeAsync(memberClient, eventId, new { enabled = true });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadIsQueueModeEnabledAsync(eventId)).Should().BeFalse();
    }

    [Fact]
    public async Task SetQueueMode_WithoutAuthentication_Returns401AndDoesNotChangeState()
    {
        var anonymousClient = _factory.CreateClient();
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(adminClient);
        var eventId = await CreateEventAsync(adminClient, venueId, seatMapId);

        var response = await PatchQueueModeAsync(anonymousClient, eventId, new { enabled = true });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ReadIsQueueModeEnabledAsync(eventId)).Should().BeFalse();
    }

    [Fact]
    public async Task SetQueueMode_WithMissingEnabledField_Returns400AndDoesNotChangeState()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(adminClient);
        var eventId = await CreateEventAsync(adminClient, venueId, seatMapId);

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
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(adminClient);
        var eventId = await CreateEventAsync(adminClient, venueId, seatMapId);

        var response = await PatchQueueModeAsync(adminClient, eventId, new { enabled = "false" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "enabled 為字串而非 boolean 時，model binding 階段就應該失敗");
        (await ReadIsQueueModeEnabledAsync(eventId)).Should().BeFalse();
    }

    // ---- GET /api/admin/events/{eventId}/sales-report（sales-report tasks.md 4.3） ----

    [Fact]
    public async Task GetSalesReport_AsAdmin_Returns200()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(adminClient);
        var eventId = await CreateEventAsync(adminClient, venueId, seatMapId);

        var response = await adminClient.GetAsync($"/api/admin/events/{eventId}/sales-report");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetSalesReport_AsNonAdminMember_Returns403()
    {
        var email = AuthTestHelper.NewEmail();
        var client = _factory.CreateClient();
        var tokens = await AuthTestHelper.RegisterAndLoginAsync(client, email);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync($"/api/admin/events/{Guid.NewGuid()}/sales-report");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

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
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);

        var response = await adminClient.GetAsync($"/api/admin/events/{Guid.NewGuid()}/sales-report");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetSalesReport_WithPaidOrder_ReturnsCorrectlySerializedJsonBody()
    {
        // 補上 DTO → ASP.NET JSON serialization 的整合驗證（Application 層測試只驗證 C# 物件本身，
        // 沒有經過真正的 HTTP 序列化路徑；用 JsonDocument 直接檢查駝峰命名的欄位是否存在，
        // 比反序列化回同一個 C# 型別更能抓到「欄位名稱不是駝峰」這類問題，因為反序列化預設對
        // 屬性名稱大小寫不敏感，PascalCase 誤寫也會反序列化成功、測不出來）。
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var (venueId, seatMapId) = await CreateVenueWithSeatMapAsync(adminClient, zoneCode: "A");
        var eventId = await CreateEventAsync(adminClient, venueId, seatMapId);
        var ticketTypeResponse = await adminClient.PostAsJsonAsync(
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

        var response = await adminClient.GetAsync($"/api/admin/events/{eventId}/sales-report");
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
