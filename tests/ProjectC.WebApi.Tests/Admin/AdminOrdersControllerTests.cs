using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using ProjectC.Application.Orders.GetOrderById;
using ProjectC.Application.Orders.GetOrders;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Admin;

public class AdminOrdersControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AdminOrdersControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private Task<OrganizerScopedTestData.SeededOrder> SeedPendingOrderAsync(HttpClient organizerClient)
        => OrganizerScopedTestData.SeedPendingOrderAsync(_factory, organizerClient);

    // 未帶 OrganizerId claim 的兩種身份：一般 Member，以及 Admin 角色但未切換 Organizer
    // （證明 Admin 角色不再能繞過 Organizer 切換）。
    private async Task<IReadOnlyList<(string Identity, HttpClient Client)>> CreateClientsWithoutOrganizerContextAsync()
        =>
        [
            ("Member", await OrganizerScopedTestData.CreateAuthenticatedMemberClientAsync(_factory)),
            ("Admin", await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory)),
        ];

    // ---- 授權規則 ----

    // [ORD-AUTHZ-001] 已切換至 Approved Organizer（非 Admin）即可呼叫列表與明細端點
    [Fact]
    public async Task GetOrdersAndGetOrderById_AsApprovedOrganizerNonAdmin_Returns200()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var seeded = await SeedPendingOrderAsync(organizerClient);

        var listResponse = await organizerClient.GetAsync("/api/admin/orders");
        var detailResponse = await organizerClient.GetAsync($"/api/admin/orders/{seeded.OrderId}");

        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        detailResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // [ORD-AUTHZ-002]
    [Fact]
    public async Task GetOrdersAndGetOrderById_WithoutOrganizerContext_Returns403ForMemberAndAdmin()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var seeded = await SeedPendingOrderAsync(organizerClient);

        foreach (var (identity, client) in await CreateClientsWithoutOrganizerContextAsync())
        {
            var listResponse = await client.GetAsync("/api/admin/orders");
            var detailResponse = await client.GetAsync($"/api/admin/orders/{seeded.OrderId}");

            listResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{identity} 未切換 Organizer 呼叫列表");
            detailResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{identity} 未切換 Organizer 呼叫明細");
        }
    }

    // [ORD-AUTHZ-003]
    [Fact]
    public async Task GetOrders_WithoutAuthentication_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/admin/orders");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // [ORD-AUTHZ-003]
    [Fact]
    public async Task GetOrderById_WithoutAuthentication_Returns401()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var seeded = await SeedPendingOrderAsync(organizerClient);
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/admin/orders/{seeded.OrderId}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // [ORD-AUTHZ-004] RequireOrganizerContext 不即時查表：停權前核發、未過期的 Access Token 在過期前仍可通過。
    // 這是 design.md Decision 2 的既知有界延遲視窗；若此測試失敗，代表 Policy 行為改變，spec 必須同步修改。
    [Fact]
    public async Task GetOrders_WithTokenIssuedBeforeOrganizerSuspended_IsStillAcceptedUntilExpiry()
    {
        var (organizerClient, organizerId) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var seeded = await SeedPendingOrderAsync(organizerClient);
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);

        var suspendResponse = await adminClient.PatchAsync($"/api/admin/organizers/{organizerId}/suspend", null);
        suspendResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var response = await organizerClient.GetAsync("/api/admin/orders");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var orders = await response.Content.ReadFromJsonAsync<List<OrderSummaryDto>>();
        orders.Should().Contain(o => o.Id == seeded.OrderId);
    }

    // ---- 訂單列表（租戶過濾） ----

    // [ORD-LIST-001]
    [Fact]
    public async Task GetOrders_ReturnsOnlyCallerOrganizerOrders()
    {
        var (organizerAClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var (organizerBClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var orderA = await SeedPendingOrderAsync(organizerAClient);
        var orderB = await SeedPendingOrderAsync(organizerBClient);

        var response = await organizerAClient.GetAsync("/api/admin/orders");

        var orders = await response.Content.ReadFromJsonAsync<List<OrderSummaryDto>>();
        orders!.Select(o => o.Id).Should().BeEquivalentTo([orderA.OrderId]);
        // SeedPendingOrderAsync 的買家以 AuthTestHelper 預設顯示名稱註冊。
        orders.Should().ContainSingle(o => o.Id == orderA.OrderId && o.Status == "Pending" && o.BuyerDisplayName == "Test User");
        orders.Should().NotContain(o => o.Id == orderB.OrderId);
    }

    // [ORD-LIST-002] 除顯示名稱外不得回傳買家 Email 等個資——以序列化後的 JSON 驗證，而非只看 DTO。
    [Fact]
    public async Task GetOrders_WithTwoBuyers_ReturnsBuyerDisplayNamesWithoutEmail()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var seededEvent = await OrderDisplayTestData.SeedEventAsync(_factory, organizerClient, "Admin Display Event", seatCount: 2, countTicketTypeCount: 0);
        var (aliceClient, aliceEmail) = await OrderDisplayTestData.CreateBuyerClientAsync(_factory, "Alice");
        var (bobClient, bobEmail) = await OrderDisplayTestData.CreateBuyerClientAsync(_factory, "Bob");
        var aliceOrderId = await OrderDisplayTestData.PlaceOrderAsync(aliceClient, seededEvent, seatItemCount: 1, countItemCount: 0, firstSeatIndex: 0);
        var bobOrderId = await OrderDisplayTestData.PlaceOrderAsync(bobClient, seededEvent, seatItemCount: 1, countItemCount: 0, firstSeatIndex: 1);

        var response = await organizerClient.GetAsync("/api/admin/orders");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var displayNameByOrderId = document.RootElement.EnumerateArray()
            .ToDictionary(order => order.GetProperty("id").GetGuid(), order => order.GetProperty("buyerDisplayName").GetString());
        displayNameByOrderId.Should().BeEquivalentTo(new Dictionary<Guid, string?> { [aliceOrderId] = "Alice", [bobOrderId] = "Bob" });
        body.Should().NotContainEquivalentOf("email").And.NotContain(aliceEmail).And.NotContain(bobEmail);
    }

    // ---- 訂單明細 ----

    // [ORD-DETAIL-001]
    [Fact]
    public async Task GetOrderById_WithExistingOrder_ReturnsDetailWithItems()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var seeded = await SeedPendingOrderAsync(organizerClient);

        var response = await organizerClient.GetAsync($"/api/admin/orders/{seeded.OrderId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = await response.Content.ReadFromJsonAsync<OrderDetailDto>();
        detail!.Id.Should().Be(seeded.OrderId);
        detail.Items.Should().HaveCount(1);
    }

    // [ORD-DETAIL-002]
    [Fact]
    public async Task GetOrderById_WithNonExistentOrder_Returns404()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);

        var response = await organizerClient.GetAsync($"/api/admin/orders/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // [ORD-DETAIL-003] 其他 Organizer 的訂單視同不存在：狀態碼與 body 皆與真正不存在時相同（除 ID 外逐字相同）
    [Fact]
    public async Task GetOrderById_WithOtherOrganizerOrder_Returns404WithSameBodyAsMissingOrder()
    {
        var (organizerAClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var (organizerBClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var orderB = await SeedPendingOrderAsync(organizerBClient);
        var missingOrderId = Guid.NewGuid();

        var otherOrganizerResponse = await organizerAClient.GetAsync($"/api/admin/orders/{orderB.OrderId}");
        var missingResponse = await organizerAClient.GetAsync($"/api/admin/orders/{missingOrderId}");

        otherOrganizerResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        missingResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var otherOrganizerBody = await NotFoundResponseBody.ReadNormalizedAsync(otherOrganizerResponse, orderB.OrderId);
        var missingBody = await NotFoundResponseBody.ReadNormalizedAsync(missingResponse, missingOrderId);
        otherOrganizerBody.Should().Be(missingBody);
        otherOrganizerBody.Should().NotContain("buyerId").And.NotContain("items");
    }
}
