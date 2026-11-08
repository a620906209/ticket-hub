using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using ProjectC.Application.Common.Paging;
using ProjectC.Application.Orders.GetOrders;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Admin;

public class AdminOrdersPagingTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AdminOrdersPagingTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    public static TheoryData<string, string[]> InvalidPagingQueries => PaginationTestSupport.InvalidPagingQueries;

    // [LP-PARAM-001][LP-RESP-001] 未帶參數：page=1、pageSize=20，回應為 camelCase 的四個欄位。
    [Fact]
    public async Task GetOrders_WithoutPagingParameters_ReturnsEnvelopeWithDefaults()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var seeded = await OrganizerScopedTestData.SeedPendingOrderAsync(_factory, organizerClient);

        var response = await organizerClient.GetAsync("/api/admin/orders");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo(["items", "page", "pageSize", "totalCount"]);
        document.RootElement.GetProperty("page").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("pageSize").GetInt32().Should().Be(20);
        document.RootElement.GetProperty("totalCount").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid().Should().Be(seeded.OrderId);
    }

    // [LP-PARAM-002] 只帶其中一個參數，另一個用預設值。
    [Fact]
    public async Task GetOrders_WithOnlyOneParameter_UsesDefaultForTheOther()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        await OrganizerScopedTestData.SeedPendingOrderAsync(_factory, organizerClient);

        var pageSizeOnly = await organizerClient.GetFromJsonAsync<PagedResultDto<OrderSummaryDto>>("/api/admin/orders?pageSize=5");
        var pageOnly = await organizerClient.GetFromJsonAsync<PagedResultDto<OrderSummaryDto>>("/api/admin/orders?page=2");

        pageSizeOnly!.Page.Should().Be(1);
        pageSizeOnly.PageSize.Should().Be(5);
        pageOnly!.Page.Should().Be(2);
        pageOnly.PageSize.Should().Be(20);
    }

    // [LP-VALID-001～008] 越界、非整數、int 溢位一律 400 Validation，errors 鍵為小寫 page／pageSize（不是 500）。
    [Theory]
    [MemberData(nameof(InvalidPagingQueries))]
    public async Task GetOrders_WithInvalidPagingParameters_Returns400ValidationWithFieldErrors(string query, string[] expectedErrorKeys)
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);

        var response = await organizerClient.GetAsync($"/api/admin/orders{query}");

        await PaginationTestSupport.AssertValidationErrorAsync(response, expectedErrorKeys);
    }

    // [LP-RESP-002～004][ORD-PAGE-001] 切頁在資料庫端完成：三筆訂單、pageSize=2 → 第 1 頁 2 筆、第 2 頁 1 筆、第 3 頁空；
    // totalCount 皆為 3（不含其他 Organizer 的訂單）；兩頁合併涵蓋全部訂單且不重複。
    [Fact]
    public async Task GetOrders_WithPageSizeTwoOverThreeOrders_SplitsIntoPagesAndKeepsTotalCount()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var (otherOrganizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        await OrganizerScopedTestData.SeedPendingOrderAsync(_factory, otherOrganizerClient);
        var seededEvent = await OrderDisplayTestData.SeedEventAsync(_factory, organizerClient, "Paging Event", seatCount: 3, countTicketTypeCount: 0);
        var (buyerClient, _) = await OrderDisplayTestData.CreateBuyerClientAsync(_factory);
        var orderIds = await PaginationTestSupport.PlaceOrdersAsync(buyerClient, seededEvent, orderCount: 3);

        var first = await organizerClient.GetFromJsonAsync<PagedResultDto<OrderSummaryDto>>("/api/admin/orders?page=1&pageSize=2");
        var second = await organizerClient.GetFromJsonAsync<PagedResultDto<OrderSummaryDto>>("/api/admin/orders?page=2&pageSize=2");
        var beyond = await organizerClient.GetFromJsonAsync<PagedResultDto<OrderSummaryDto>>("/api/admin/orders?page=3&pageSize=2");

        first!.Items.Should().HaveCount(2);
        second!.Items.Should().HaveCount(1);
        first.Items.Concat(second.Items).Select(o => o.Id).Should().BeEquivalentTo(orderIds);
        new[] { first.TotalCount, second.TotalCount, beyond!.TotalCount }.Should().AllBeEquivalentTo(3);
        beyond.Items.Should().BeEmpty();
        beyond.Page.Should().Be(3);
        beyond.PageSize.Should().Be(2);
    }
}
