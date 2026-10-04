using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using ProjectC.Application.Events.GetEventSeats;
using ProjectC.Application.Events.CreateEvent;
using ProjectC.Application.Orders.PlaceOrder;
using ProjectC.Application.Tickets.CreateTicketType;
using ProjectC.Application.Tickets.GetTicketTypes;
using ProjectC.Application.Venues.CreateSeatMap;
using ProjectC.Application.Venues.CreateVenue;
using ProjectC.WebApi.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Domain.Events;
using ProjectC.Domain.Orders;
using ProjectC.Domain.PurchaseQueue;
using ProjectC.Infrastructure.Persistence;

namespace ProjectC.WebApi.Tests.Orders;

public class OrdersControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public OrdersControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<Guid> ReadCreatedIdAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreatedResponse>();
        return created!.Id;
    }

    /// <summary>建立一場活動，含一個 A 區座位與對應票種，回傳 (EventId, EventSeatId, TicketTypeId)。</summary>
    private async Task<(Guid EventId, Guid EventSeatId, Guid TicketTypeId)> SeedEventWithSeatAndTicketTypeAsync(string zoneCode = "A")
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(_factory);

        var venueResponse = await adminClient.PostAsJsonAsync("/api/admin/venues", new CreateVenueRequest("Test Venue"));
        var venueId = await ReadCreatedIdAsync(venueResponse);

        var seatMapResponse = await adminClient.PostAsJsonAsync(
            $"/api/admin/venues/{venueId}/seat-maps",
            new CreateSeatMapRequest([new SeatRequest(zoneCode, "1")]));
        var seatMapId = await ReadCreatedIdAsync(seatMapResponse);

        var eventResponse = await adminClient.PostAsJsonAsync(
            "/api/admin/events",
            new CreateEventRequest("Concert", DateTime.UtcNow.AddDays(30), venueId, seatMapId));
        var eventId = await ReadCreatedIdAsync(eventResponse);

        var ticketTypeResponse = await adminClient.PostAsJsonAsync(
            $"/api/admin/events/{eventId}/ticket-types",
            new CreateTicketTypeRequest(zoneCode, 500m));
        var ticketTypeId = await ReadCreatedIdAsync(ticketTypeResponse);

        var publicClient = _factory.CreateClient();
        var seatsResponse = await publicClient.GetAsync($"/api/events/{eventId}/seats");
        var seats = await seatsResponse.Content.ReadFromJsonAsync<List<EventSeatDto>>();
        var eventSeatId = seats!.Single(s => s.ZoneCode == zoneCode).EventSeatId;

        return (eventId, eventSeatId, ticketTypeId);
    }

    private async Task<HttpClient> CreateAuthenticatedMemberClientAsync()
    {
        var client = _factory.CreateClient();
        var tokens = await AuthTestHelper.RegisterAndLoginAsync(client);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    // ---- 買家需登入 ----

    [Fact]
    public async Task PlaceOrder_WithoutAuthentication_Returns401()
    {
        var (_, eventSeatId, ticketTypeId) = await SeedEventWithSeatAndTicketTypeAsync();
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/orders",
            new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeatId, ticketTypeId)]));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- 建立訂單 ----

    [Fact]
    public async Task PlaceOrder_WithAvailableSeatAndMatchingTicketType_ReturnsCreatedAndHoldsSeat()
    {
        var (eventId, eventSeatId, ticketTypeId) = await SeedEventWithSeatAndTicketTypeAsync();
        var buyerClient = await CreateAuthenticatedMemberClientAsync();

        var response = await buyerClient.PostAsJsonAsync(
            "/api/orders",
            new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeatId, ticketTypeId)]));

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var publicClient = _factory.CreateClient();
        var seatsResponse = await publicClient.GetAsync($"/api/events/{eventId}/seats");
        var seats = await seatsResponse.Content.ReadFromJsonAsync<List<EventSeatDto>>();
        seats!.Single(s => s.EventSeatId == eventSeatId).Status.Should().Be("Held");
    }

    [Fact]
    public async Task PlaceOrder_WithLegacyPayloadMissingQuantity_TreatsAsOneAndHoldsSeat()
    {
        // 外部審查第四輪抓到的阻斷問題：MUST 用匿名物件送出只有舊欄位的原始 JSON，
        // 用強型別 PlaceOrderSelectionRequest 物件建構測不出「欄位缺失」這個情境
        // （強型別物件永遠會序列化出 Quantity 的預設值，不是真的缺欄位）。
        var (eventId, eventSeatId, ticketTypeId) = await SeedEventWithSeatAndTicketTypeAsync();
        var buyerClient = await CreateAuthenticatedMemberClientAsync();

        var response = await buyerClient.PostAsJsonAsync(
            "/api/orders",
            new { Selections = new[] { new { EventSeatId = eventSeatId, TicketTypeId = ticketTypeId } } });

        response.StatusCode.Should().Be(HttpStatusCode.Created,
            "缺 Quantity 欄位的舊格式座位選購請求 MUST 視為購買數量 1，成功建立訂單");

        var publicClient = _factory.CreateClient();
        var seatsResponse = await publicClient.GetAsync($"/api/events/{eventId}/seats");
        var seats = await seatsResponse.Content.ReadFromJsonAsync<List<EventSeatDto>>();
        seats!.Single(s => s.EventSeatId == eventSeatId).Status.Should().Be("Held");
    }

    [Fact]
    public async Task PlaceOrder_WithNonExistentSeatOrTicketType_Returns404()
    {
        var (_, eventSeatId, _) = await SeedEventWithSeatAndTicketTypeAsync();
        var buyerClient = await CreateAuthenticatedMemberClientAsync();

        var response = await buyerClient.PostAsJsonAsync(
            "/api/orders",
            new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeatId, Guid.NewGuid())]));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PlaceOrder_WithSeatAlreadyHeldByAnotherOrder_ReturnsConflict()
    {
        var (_, eventSeatId, ticketTypeId) = await SeedEventWithSeatAndTicketTypeAsync();
        var firstBuyer = await CreateAuthenticatedMemberClientAsync();
        var secondBuyer = await CreateAuthenticatedMemberClientAsync();
        var request = new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeatId, ticketTypeId)]);

        var firstResponse = await firstBuyer.PostAsJsonAsync("/api/orders", request);
        firstResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var secondResponse = await secondBuyer.PostAsJsonAsync("/api/orders", request);

        secondResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task PlaceOrder_WithSeatAndTicketTypeFromDifferentEvents_Returns400()
    {
        var (_, eventSeatId, _) = await SeedEventWithSeatAndTicketTypeAsync(zoneCode: "A");
        var (_, _, otherEventTicketTypeId) = await SeedEventWithSeatAndTicketTypeAsync(zoneCode: "A");

        var buyerClient = await CreateAuthenticatedMemberClientAsync();
        var response = await buyerClient.PostAsJsonAsync(
            "/api/orders",
            new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeatId, otherEventTicketTypeId)]));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PlaceOrder_WithSeatZoneNotMatchingTicketTypeZoneWithinSameEvent_Returns400()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(_factory);
        var venueResponse = await adminClient.PostAsJsonAsync("/api/admin/venues", new CreateVenueRequest("Two Zone Venue"));
        var venueId = await ReadCreatedIdAsync(venueResponse);
        var seatMapResponse = await adminClient.PostAsJsonAsync(
            $"/api/admin/venues/{venueId}/seat-maps",
            new CreateSeatMapRequest([new SeatRequest("A", "1"), new SeatRequest("B", "1")]));
        var seatMapId = await ReadCreatedIdAsync(seatMapResponse);
        var eventResponse = await adminClient.PostAsJsonAsync(
            "/api/admin/events", new CreateEventRequest("Two Zone Concert", DateTime.UtcNow.AddDays(30), venueId, seatMapId));
        var eventId = await ReadCreatedIdAsync(eventResponse);
        var ticketTypeBResponse = await adminClient.PostAsJsonAsync(
            $"/api/admin/events/{eventId}/ticket-types", new CreateTicketTypeRequest("B", 500m));
        var ticketTypeBId = await ReadCreatedIdAsync(ticketTypeBResponse);

        var publicClient = _factory.CreateClient();
        var seatsResponse = await publicClient.GetAsync($"/api/events/{eventId}/seats");
        var seats = await seatsResponse.Content.ReadFromJsonAsync<List<EventSeatDto>>();
        var seatAId = seats!.Single(s => s.ZoneCode == "A").EventSeatId;

        var buyerClient = await CreateAuthenticatedMemberClientAsync();
        var response = await buyerClient.PostAsJsonAsync(
            "/api/orders",
            new PlaceOrderRequest([new PlaceOrderSelectionRequest(seatAId, ticketTypeBId)]));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- 確認訂單 ----

    [Fact]
    public async Task ConfirmOrder_ByBuyerOnOwnPendingOrder_Returns204AndSellsSeat()
    {
        var (eventId, eventSeatId, ticketTypeId) = await SeedEventWithSeatAndTicketTypeAsync();
        var buyerClient = await CreateAuthenticatedMemberClientAsync();
        var placeResponse = await buyerClient.PostAsJsonAsync(
            "/api/orders", new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeatId, ticketTypeId)]));
        var orderId = await ReadCreatedIdAsync(placeResponse);

        var response = await buyerClient.PostAsync($"/api/orders/{orderId}/confirm", null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var publicClient = _factory.CreateClient();
        var seatsResponse = await publicClient.GetAsync($"/api/events/{eventId}/seats");
        var seats = await seatsResponse.Content.ReadFromJsonAsync<List<EventSeatDto>>();
        seats!.Single(s => s.EventSeatId == eventSeatId).Status.Should().Be("Sold");
    }

    [Fact]
    public async Task ConfirmOrder_ByNonBuyer_Returns403()
    {
        var (_, eventSeatId, ticketTypeId) = await SeedEventWithSeatAndTicketTypeAsync();
        var buyerClient = await CreateAuthenticatedMemberClientAsync();
        var otherClient = await CreateAuthenticatedMemberClientAsync();
        var placeResponse = await buyerClient.PostAsJsonAsync(
            "/api/orders", new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeatId, ticketTypeId)]));
        var orderId = await ReadCreatedIdAsync(placeResponse);

        var response = await otherClient.PostAsync($"/api/orders/{orderId}/confirm", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ConfirmOrder_WithNonExistentOrder_Returns404()
    {
        var buyerClient = await CreateAuthenticatedMemberClientAsync();

        var response = await buyerClient.PostAsync($"/api/orders/{Guid.NewGuid()}/confirm", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- 取消訂單 ----

    [Fact]
    public async Task CancelOrder_ByBuyerOnOwnPendingOrder_Returns204AndReleasesSeat()
    {
        var (eventId, eventSeatId, ticketTypeId) = await SeedEventWithSeatAndTicketTypeAsync();
        var buyerClient = await CreateAuthenticatedMemberClientAsync();
        var placeResponse = await buyerClient.PostAsJsonAsync(
            "/api/orders", new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeatId, ticketTypeId)]));
        var orderId = await ReadCreatedIdAsync(placeResponse);

        var response = await buyerClient.PostAsync($"/api/orders/{orderId}/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var publicClient = _factory.CreateClient();
        var seatsResponse = await publicClient.GetAsync($"/api/events/{eventId}/seats");
        var seats = await seatsResponse.Content.ReadFromJsonAsync<List<EventSeatDto>>();
        seats!.Single(s => s.EventSeatId == eventSeatId).Status.Should().Be("Available");
    }

    [Fact]
    public async Task CancelOrder_ByNonBuyer_Returns403()
    {
        var (_, eventSeatId, ticketTypeId) = await SeedEventWithSeatAndTicketTypeAsync();
        var buyerClient = await CreateAuthenticatedMemberClientAsync();
        var otherClient = await CreateAuthenticatedMemberClientAsync();
        var placeResponse = await buyerClient.PostAsJsonAsync(
            "/api/orders", new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeatId, ticketTypeId)]));
        var orderId = await ReadCreatedIdAsync(placeResponse);

        var response = await otherClient.PostAsync($"/api/orders/{orderId}/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CancelOrder_WithNonExistentOrder_Returns404()
    {
        var buyerClient = await CreateAuthenticatedMemberClientAsync();

        var response = await buyerClient.PostAsync($"/api/orders/{Guid.NewGuid()}/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetMyOrders_WithoutAuthentication_Returns401()
    {
        var response = await _factory.CreateClient().GetAsync("/api/orders");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetMyOrdersAndDetail_WhenBuyerOwnsConfirmedOrder_Returns200()
    {
        var (_, eventSeatId, ticketTypeId) = await SeedEventWithSeatAndTicketTypeAsync();
        var buyerClient = await CreateAuthenticatedMemberClientAsync();
        var placeResponse = await buyerClient.PostAsJsonAsync(
            "/api/orders", new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeatId, ticketTypeId)]));
        var orderId = await ReadCreatedIdAsync(placeResponse);
        var confirmResponse = await buyerClient.PostAsync($"/api/orders/{orderId}/confirm", null);
        confirmResponse.EnsureSuccessStatusCode();

        var listResponse = await buyerClient.GetAsync("/api/orders");
        var detailResponse = await buyerClient.GetAsync($"/api/orders/{orderId}");

        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        detailResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // [BOQ-DETAIL-DISPLAY-001] 驗證序列化後的欄位名稱與值（單元測試只驗證 DTO 內容）。
    [Fact]
    public async Task GetMyOrderDetail_WithSeatAndCountItems_ReturnsDisplayFieldsInJson()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var seededEvent = await OrderDisplayTestData.SeedEventAsync(_factory, organizerClient, "Display Concert", seatCount: 1, countTicketTypeCount: 1);
        var (buyerClient, _) = await OrderDisplayTestData.CreateBuyerClientAsync(_factory);
        var orderId = await OrderDisplayTestData.PlaceOrderAsync(buyerClient, seededEvent, seatItemCount: 1, countItemCount: 1);

        var response = await buyerClient.GetAsync($"/api/orders/{orderId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("eventTitle").GetString().Should().Be("Display Concert");
        var items = document.RootElement.GetProperty("items").EnumerateArray().ToList();
        var seatItem = items.Single(item => item.GetProperty("eventSeatId").ValueKind != JsonValueKind.Null);
        seatItem.GetProperty("seatZoneCode").GetString().Should().Be("A");
        seatItem.GetProperty("seatNumber").GetString().Should().Be("1");
        seatItem.GetProperty("ticketTypeName").GetString().Should().Be("A");
        var countItem = items.Single(item => item.GetProperty("eventSeatId").ValueKind == JsonValueKind.Null);
        countItem.GetProperty("seatZoneCode").ValueKind.Should().Be(JsonValueKind.Null);
        countItem.GetProperty("seatNumber").ValueKind.Should().Be(JsonValueKind.Null);
        countItem.GetProperty("ticketTypeName").GetString().Should().Be(OrderDisplayTestData.CountTicketTypeName(0));
    }

    // [BOQ-LIST-TITLE-001]
    [Fact]
    public async Task GetMyOrders_WithOrdersInTwoEvents_ReturnsEventTitleInJson()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var firstEvent = await OrderDisplayTestData.SeedEventAsync(_factory, organizerClient, "First Concert", seatCount: 1, countTicketTypeCount: 0);
        var secondEvent = await OrderDisplayTestData.SeedEventAsync(_factory, organizerClient, "Second Concert", seatCount: 1, countTicketTypeCount: 0);
        var (buyerClient, _) = await OrderDisplayTestData.CreateBuyerClientAsync(_factory);
        var firstOrderId = await OrderDisplayTestData.PlaceOrderAsync(buyerClient, firstEvent, seatItemCount: 1, countItemCount: 0);
        var secondOrderId = await OrderDisplayTestData.PlaceOrderAsync(buyerClient, secondEvent, seatItemCount: 1, countItemCount: 0);

        var response = await buyerClient.GetAsync("/api/orders");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var titleByOrderId = document.RootElement.EnumerateArray()
            .ToDictionary(order => order.GetProperty("id").GetGuid(), order => order.GetProperty("eventTitle").GetString());
        titleByOrderId.Should().BeEquivalentTo(new Dictionary<Guid, string?> { [firstOrderId] = "First Concert", [secondOrderId] = "Second Concert" });
    }

    // [BOQ-DETAIL-003]
    [Fact]
    public async Task GetMyOrderDetail_ByNonBuyer_Returns403()
    {
        var (_, eventSeatId, ticketTypeId) = await SeedEventWithSeatAndTicketTypeAsync();
        var buyerClient = await CreateAuthenticatedMemberClientAsync();
        var otherClient = await CreateAuthenticatedMemberClientAsync();
        var placeResponse = await buyerClient.PostAsJsonAsync(
            "/api/orders", new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeatId, ticketTypeId)]));
        var orderId = await ReadCreatedIdAsync(placeResponse);

        var response = await otherClient.GetAsync($"/api/orders/{orderId}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("items").And.NotContain("eventTitle");
    }

    // [BOQ-DETAIL-004]
    [Fact]
    public async Task GetMyOrderDetail_WithNonExistentOrder_Returns404()
    {
        var buyerClient = await CreateAuthenticatedMemberClientAsync();

        var response = await buyerClient.GetAsync($"/api/orders/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- real-name-verification：下單實名閘門（TP-RN-ORDER-*、RNV-ERROR-001） ----

    private sealed record InventorySnapshot(EventSeatStatus SeatStatus, int? AvailableQuantity, int OrderCount);

    private async Task<InventorySnapshot> ReadInventorySnapshotAsync(RealNameTestData.SeededEvent seededEvent)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var eventSeat = await dbContext.EventSeats.AsNoTracking().SingleAsync(s => s.Id == seededEvent.EventSeatId);
        var ticketType = await dbContext.TicketTypes.AsNoTracking().SingleAsync(t => t.Id == seededEvent.TicketTypeId);
        var orderCount = await dbContext.Orders.AsNoTracking().CountAsync(o => o.EventId == seededEvent.EventId);
        return new InventorySnapshot(eventSeat.GetStatus(DateTime.UtcNow), ticketType.AvailableQuantity, orderCount);
    }

    private static readonly string[] StandardProblemDetailsFields = ["type", "title", "status", "detail", "instance", "traceId"];

    // [TP-RN-ORDER-001]／[RNV-ERROR-001] 閘門必須在鎖座位與扣庫存之前擋下；錯誤 body 只有標準 ProblemDetails 欄位。
    [Fact]
    public async Task PlaceOrder_WhenEventRequiresRealNameAndBuyerUnregistered_Returns403WithoutTouchingInventory()
    {
        var organizerClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(_factory);
        var seededEvent = await RealNameTestData.SeedEventAsync(_factory, organizerClient, isRealNameRequired: true);
        var buyer = await RealNameTestData.CreateMemberAsync(_factory);
        var before = await ReadInventorySnapshotAsync(seededEvent);

        var response = await RealNameTestData.PlaceOrderAsync(buyer.Client, seededEvent);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await RealNameTestData.ReadProblemTitleAsync(response)).Should().Be("RealNameRequired");
        var after = await ReadInventorySnapshotAsync(seededEvent);
        after.Should().Be(before);
        after.SeatStatus.Should().Be(EventSeatStatus.Available);
        after.OrderCount.Should().Be(0);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.EnumerateObject().Select(p => p.Name).Should().BeSubsetOf(StandardProblemDetailsFields);
        body.RootElement.GetProperty("detail").GetString().Should()
            .NotContainEquivalentOf("nationalId").And.NotContainEquivalentOf(buyer.MemberId.ToString());
    }

    // [TP-RN-ORDER-002]
    [Fact]
    public async Task PlaceOrder_WhenEventRequiresRealNameAndBuyerRegistered_Returns201AndCreatesOrder()
    {
        var organizerClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(_factory);
        var seededEvent = await RealNameTestData.SeedEventAsync(_factory, organizerClient, isRealNameRequired: true);
        var buyer = await RealNameTestData.CreateRegisteredMemberAsync(_factory);

        var response = await RealNameTestData.PlaceOrderAsync(buyer.Client, seededEvent);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await ReadInventorySnapshotAsync(seededEvent)).OrderCount.Should().Be(1);
    }

    // [TP-RN-ORDER-003] 不需實名的活動維持上線前行為。
    [Fact]
    public async Task PlaceOrder_WhenEventDoesNotRequireRealNameAndBuyerUnregistered_Returns201()
    {
        var organizerClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(_factory);
        var seededEvent = await RealNameTestData.SeedEventAsync(_factory, organizerClient, isRealNameRequired: false);
        var buyer = await RealNameTestData.CreateMemberAsync(_factory);

        var response = await RealNameTestData.PlaceOrderAsync(buyer.Client, seededEvent);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await ReadInventorySnapshotAsync(seededEvent)).OrderCount.Should().Be(1);
    }

    // [TP-RN-ORDER-005] 已取得入場資格不代表可略過實名；被擋下時不得消耗入場資格。
    [Fact]
    public async Task PlaceOrder_WhenUnregisteredBuyerIsAdmitted_Returns403AndLeavesQueueEntryUntouched()
    {
        var organizerClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(_factory);
        var seededEvent = await RealNameTestData.SeedEventAsync(_factory, organizerClient, isRealNameRequired: true, isQueueModeEnabled: true);
        var buyer = await RealNameTestData.CreateMemberAsync(_factory);
        var now = DateTime.UtcNow;
        var entry = new PurchaseQueueEntry(Guid.NewGuid(), seededEvent.EventId, buyer.MemberId, now.AddMinutes(-5));
        entry.Admit(now, now.AddMinutes(30));
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            dbContext.PurchaseQueueEntries.Add(entry);
            await dbContext.SaveChangesAsync();
        }

        var response = await RealNameTestData.PlaceOrderAsync(buyer.Client, seededEvent);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await RealNameTestData.ReadProblemTitleAsync(response)).Should().Be("RealNameRequired");
        using var readScope = _factory.Services.CreateScope();
        var readContext = readScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var storedEntry = await readContext.PurchaseQueueEntries.AsNoTracking().SingleAsync(e => e.Id == entry.Id);
        storedEntry.Status.Should().Be(PurchaseQueueEntryStatus.Admitted);
        storedEntry.AdmissionExpiresAtUtc.Should().BeCloseTo(entry.AdmissionExpiresAtUtc!.Value, TimeSpan.FromMilliseconds(1));
    }

    // [TP-RN-ORDER-006] 跨活動項目屬於請求格式錯誤，必須先於實名閘門回既有 400，且不動到任何庫存。
    [Fact]
    public async Task PlaceOrder_WhenItemsSpanEventsIncludingRealNameEvent_Returns400ValidationWithoutTouchingInventory()
    {
        var organizerClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(_factory);
        var realNameEvent = await RealNameTestData.SeedEventAsync(_factory, organizerClient, isRealNameRequired: true);
        var otherEvent = await RealNameTestData.SeedEventAsync(_factory, organizerClient, isRealNameRequired: false);
        var buyer = await RealNameTestData.CreateMemberAsync(_factory);
        var realNameBefore = await ReadInventorySnapshotAsync(realNameEvent);
        var otherBefore = await ReadInventorySnapshotAsync(otherEvent);

        var response = await buyer.Client.PostAsJsonAsync("/api/orders", new PlaceOrderRequest(
        [
            new PlaceOrderSelectionRequest(realNameEvent.EventSeatId, realNameEvent.TicketTypeId),
            new PlaceOrderSelectionRequest(otherEvent.EventSeatId, otherEvent.TicketTypeId),
        ]));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RealNameTestData.ReadProblemTitleAsync(response)).Should().NotBe("RealNameRequired");
        (await ReadInventorySnapshotAsync(realNameEvent)).Should().Be(realNameBefore);
        (await ReadInventorySnapshotAsync(otherEvent)).Should().Be(otherBefore);
    }

    // ---- event-sales-window：下單販售期間檢查（TP-SALES-ORDER-*） ----

    private async Task<RealNameTestData.SeededEvent> SeedSalesWindowEventAsync(
        DateTime? salesStartAtUtc = null, DateTime? salesEndAtUtc = null, bool isQueueModeEnabled = false)
    {
        var organizerClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(_factory);
        return await RealNameTestData.SeedEventAsync(
            _factory, organizerClient, isRealNameRequired: false, isQueueModeEnabled: isQueueModeEnabled,
            salesStartAtUtc: salesStartAtUtc, salesEndAtUtc: salesEndAtUtc);
    }

    /// <summary>Event 沒有修改販售期間的 Domain 方法，測試資料準備直接改欄位，模擬「下單後活動已停售」。</summary>
    private async Task CloseSalesAsync(Guid eventId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var closedSalesEndAtUtc = DateTime.UtcNow.AddHours(-2);
        await dbContext.Events.Where(e => e.Id == eventId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(e => e.SalesEndAtUtc, closedSalesEndAtUtc));
    }

    private async Task<(RealNameTestData.SeededEvent SeededEvent, RealNameTestData.SeededMember Buyer, Guid OrderId)> PlacePendingOrderThenCloseSalesAsync()
    {
        var seededEvent = await SeedSalesWindowEventAsync();
        var buyer = await RealNameTestData.CreateMemberAsync(_factory);
        var orderId = await ReadCreatedIdAsync(await RealNameTestData.PlaceOrderAsync(buyer.Client, seededEvent));
        await CloseSalesAsync(seededEvent.EventId);
        return (seededEvent, buyer, orderId);
    }

    private async Task AssertSalesClosedBeforeActionAsync(Guid eventId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var storedEvent = await dbContext.Events.AsNoTracking().SingleAsync(e => e.Id == eventId);
        storedEvent.SalesEndAtUtc.Should().BeBefore(DateTime.UtcNow, "前置條件：操作當下活動已停售");
    }

    // [TP-SALES-ORDER-001]
    [Fact]
    public async Task PlaceOrder_BeforeSalesStart_Returns409SalesNotOpenAndChangesNothing()
    {
        var seededEvent = await SeedSalesWindowEventAsync(salesStartAtUtc: DateTime.UtcNow.AddHours(5));
        var buyer = await RealNameTestData.CreateMemberAsync(_factory);
        var before = await ReadInventorySnapshotAsync(seededEvent);

        var response = await RealNameTestData.PlaceOrderAsync(buyer.Client, seededEvent);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await RealNameTestData.ReadProblemTitleAsync(response)).Should().Be("SalesNotOpen");
        var after = await ReadInventorySnapshotAsync(seededEvent);
        after.Should().Be(before);
        after.SeatStatus.Should().Be(EventSeatStatus.Available);
        after.OrderCount.Should().Be(0);
    }

    // [TP-SALES-ORDER-002]
    [Fact]
    public async Task PlaceOrder_AfterSalesEnd_Returns409SalesClosedAndChangesNothing()
    {
        var seededEvent = await SeedSalesWindowEventAsync(
            salesStartAtUtc: DateTime.UtcNow.AddHours(-10), salesEndAtUtc: DateTime.UtcNow.AddHours(-5));
        var buyer = await RealNameTestData.CreateMemberAsync(_factory);
        var before = await ReadInventorySnapshotAsync(seededEvent);

        var response = await RealNameTestData.PlaceOrderAsync(buyer.Client, seededEvent);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await RealNameTestData.ReadProblemTitleAsync(response)).Should().Be("SalesClosed");
        var after = await ReadInventorySnapshotAsync(seededEvent);
        after.Should().Be(before);
        after.SeatStatus.Should().Be(EventSeatStatus.Available);
        after.OrderCount.Should().Be(0);
    }

    // [TP-SALES-ORDER-011] 販售期間只限制建立新訂單；停售前建立的 Pending 訂單仍可完成付款。
    [Fact]
    public async Task ConfirmOrder_OnSalesClosedEvent_ReturnsPaidAndIssuesTickets()
    {
        var (seededEvent, buyer, orderId) = await PlacePendingOrderThenCloseSalesAsync();
        await AssertSalesClosedBeforeActionAsync(seededEvent.EventId);

        var response = await buyer.Client.PostAsync($"/api/orders/{orderId}/confirm", null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await dbContext.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId)).Status.Should().Be(OrderStatus.Paid);
        var orderItemIds = await dbContext.OrderItems.AsNoTracking()
            .Where(i => EF.Property<Guid>(i, "OrderId") == orderId)
            .Select(i => i.Id)
            .ToListAsync();
        (await dbContext.Tickets.AsNoTracking().CountAsync(t => orderItemIds.Contains(t.OrderItemId))).Should().Be(1);
        (await ReadInventorySnapshotAsync(seededEvent)).SeatStatus.Should().Be(EventSeatStatus.Sold);
    }

    // [TP-SALES-ORDER-012] 停售後仍可取消 Pending 訂單，座位依既有規則釋放。
    [Fact]
    public async Task CancelOrder_OnSalesClosedEvent_ReturnsCancelledAndReleasesInventory()
    {
        var (seededEvent, buyer, orderId) = await PlacePendingOrderThenCloseSalesAsync();
        await AssertSalesClosedBeforeActionAsync(seededEvent.EventId);

        var response = await buyer.Client.PostAsync($"/api/orders/{orderId}/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await dbContext.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId)).Status.Should().Be(OrderStatus.Cancelled);
        (await ReadInventorySnapshotAsync(seededEvent)).SeatStatus.Should().Be(EventSeatStatus.Available);
    }

    // [TP-SALES-ORDER-010] 已取得入場資格不代表可在停售後下單；被擋下時不得消耗入場資格。
    [Fact]
    public async Task PlaceOrder_WhenAdmittedButSalesClosed_Returns409SalesClosedAndKeepsQueueEntry()
    {
        var seededEvent = await SeedSalesWindowEventAsync(
            salesStartAtUtc: DateTime.UtcNow.AddHours(-10), salesEndAtUtc: DateTime.UtcNow.AddHours(-5), isQueueModeEnabled: true);
        var buyer = await RealNameTestData.CreateMemberAsync(_factory);
        var now = DateTime.UtcNow;
        var entry = new PurchaseQueueEntry(Guid.NewGuid(), seededEvent.EventId, buyer.MemberId, now.AddHours(-6));
        entry.Admit(now.AddHours(-1), now.AddHours(3));
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            dbContext.PurchaseQueueEntries.Add(entry);
            await dbContext.SaveChangesAsync();
        }
        var before = await ReadInventorySnapshotAsync(seededEvent);

        var response = await RealNameTestData.PlaceOrderAsync(buyer.Client, seededEvent);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await RealNameTestData.ReadProblemTitleAsync(response)).Should().Be("SalesClosed");
        var after = await ReadInventorySnapshotAsync(seededEvent);
        after.Should().Be(before);
        after.SeatStatus.Should().Be(EventSeatStatus.Available);
        after.OrderCount.Should().Be(0, "資料庫無該會員在此活動的訂單");
        using var readScope = _factory.Services.CreateScope();
        var readContext = readScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var storedEntry = await readContext.PurchaseQueueEntries.AsNoTracking().SingleAsync(e => e.Id == entry.Id);
        storedEntry.Status.Should().Be(PurchaseQueueEntryStatus.Admitted);
        storedEntry.AdmissionExpiresAtUtc.Should().BeCloseTo(entry.AdmissionExpiresAtUtc!.Value, TimeSpan.FromMilliseconds(1));
    }
}
