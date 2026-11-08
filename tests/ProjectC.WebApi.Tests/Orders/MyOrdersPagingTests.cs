using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using ProjectC.Application.Common.Paging;
using ProjectC.Application.Orders.GetMyOrders;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Orders;

public class MyOrdersPagingTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public MyOrdersPagingTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    public static TheoryData<string, string[]> InvalidPagingQueries => PaginationTestSupport.InvalidPagingQueries;

    private async Task<(HttpClient BuyerClient, IReadOnlyList<Guid> OrderIds)> SeedBuyerWithOrdersAsync(int orderCount)
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var seededEvent = await OrderDisplayTestData.SeedEventAsync(_factory, organizerClient, "My Paging Event", seatCount: Math.Max(orderCount, 1), countTicketTypeCount: 0);
        var (buyerClient, _) = await OrderDisplayTestData.CreateBuyerClientAsync(_factory);
        var orderIds = await PaginationTestSupport.PlaceOrdersAsync(buyerClient, seededEvent, orderCount);
        return (buyerClient, orderIds);
    }

    // [LP-PARAM-001][LP-RESP-001] 未帶參數：page=1、pageSize=20，回應為 camelCase 的四個欄位。
    [Fact]
    public async Task GetMyOrders_WithoutPagingParameters_ReturnsEnvelopeWithDefaults()
    {
        var (buyerClient, orderIds) = await SeedBuyerWithOrdersAsync(orderCount: 1);

        var response = await buyerClient.GetAsync("/api/orders");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo(["items", "page", "pageSize", "totalCount"]);
        document.RootElement.GetProperty("page").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("pageSize").GetInt32().Should().Be(20);
        document.RootElement.GetProperty("totalCount").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid().Should().Be(orderIds[0]);
    }

    // [LP-PARAM-002] 只帶其中一個參數，另一個用預設值。
    [Fact]
    public async Task GetMyOrders_WithOnlyOneParameter_UsesDefaultForTheOther()
    {
        var (buyerClient, _) = await SeedBuyerWithOrdersAsync(orderCount: 1);

        var pageSizeOnly = await buyerClient.GetFromJsonAsync<PagedResultDto<MyOrderSummaryDto>>("/api/orders?pageSize=5");
        var pageOnly = await buyerClient.GetFromJsonAsync<PagedResultDto<MyOrderSummaryDto>>("/api/orders?page=2");

        pageSizeOnly!.Page.Should().Be(1);
        pageSizeOnly.PageSize.Should().Be(5);
        pageOnly!.Page.Should().Be(2);
        pageOnly.PageSize.Should().Be(20);
    }

    // [LP-VALID-002][LP-VALID-007] 邊界值合法：pageSize=100 與 page=10000&pageSize=100 皆回 200，後者資料不足時 items 為空。
    [Fact]
    public async Task GetMyOrders_WithUpperBoundPagingParameters_Returns200()
    {
        var (buyerClient, _) = await SeedBuyerWithOrdersAsync(orderCount: 1);

        var maxPageSize = await buyerClient.GetFromJsonAsync<PagedResultDto<MyOrderSummaryDto>>("/api/orders?pageSize=100");
        var maxPage = await buyerClient.GetFromJsonAsync<PagedResultDto<MyOrderSummaryDto>>("/api/orders?page=10000&pageSize=100");

        maxPageSize!.PageSize.Should().Be(100);
        maxPageSize.Items.Should().HaveCount(1);
        maxPage!.Page.Should().Be(10000);
        maxPage.PageSize.Should().Be(100);
        maxPage.Items.Should().BeEmpty();
        maxPage.TotalCount.Should().Be(1);
    }

    // [LP-VALID-001～008] 越界、非整數、int 溢位一律 400 Validation，errors 鍵為小寫 page／pageSize（不是 500）。
    [Theory]
    [MemberData(nameof(InvalidPagingQueries))]
    public async Task GetMyOrders_WithInvalidPagingParameters_Returns400ValidationWithFieldErrors(string query, string[] expectedErrorKeys)
    {
        var buyerClient = await OrganizerScopedTestData.CreateAuthenticatedMemberClientAsync(_factory);

        var response = await buyerClient.GetAsync($"/api/orders{query}");

        await PaginationTestSupport.AssertValidationErrorAsync(response, expectedErrorKeys);
    }

    // [LP-RESP-002～004][BOQ-LIST-001] 三筆訂單、pageSize=2 → 第 1 頁 2 筆、第 2 頁 1 筆、第 3 頁空；totalCount 皆為 3
    // （不含其他買家的訂單）；兩頁合併涵蓋全部訂單且不重複。
    [Fact]
    public async Task GetMyOrders_WithPageSizeTwoOverThreeOrders_SplitsIntoPagesAndKeepsTotalCount()
    {
        var (otherBuyerClient, _) = await SeedBuyerWithOrdersAsync(orderCount: 1);
        var (buyerClient, orderIds) = await SeedBuyerWithOrdersAsync(orderCount: 3);

        var first = await buyerClient.GetFromJsonAsync<PagedResultDto<MyOrderSummaryDto>>("/api/orders?page=1&pageSize=2");
        var second = await buyerClient.GetFromJsonAsync<PagedResultDto<MyOrderSummaryDto>>("/api/orders?page=2&pageSize=2");
        var beyond = await buyerClient.GetFromJsonAsync<PagedResultDto<MyOrderSummaryDto>>("/api/orders?page=3&pageSize=2");
        var otherBuyerPage = await otherBuyerClient.GetFromJsonAsync<PagedResultDto<MyOrderSummaryDto>>("/api/orders");

        first!.Items.Should().HaveCount(2);
        second!.Items.Should().HaveCount(1);
        first.Items.Concat(second.Items).Select(o => o.Id).Should().BeEquivalentTo(orderIds);
        new[] { first.TotalCount, second.TotalCount, beyond!.TotalCount }.Should().AllBeEquivalentTo(3);
        beyond.Items.Should().BeEmpty();
        beyond.Page.Should().Be(3);
        beyond.PageSize.Should().Be(2);
        otherBuyerPage!.TotalCount.Should().Be(1);
    }
}
