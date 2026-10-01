using System.Net;
using FluentAssertions;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Orders;

/// <summary>
/// 「顯示資訊的查詢次數不隨筆數成長」以實際送到資料庫的查詢驗證（order-display-enrichment design.md 安全確認-資料庫）：
/// 單元測試只能數 Fake 被呼叫幾次，repository 實作內部若逐筆查詢、或誤用會載入整張座位圖的方法，仍會通過。
/// 每次計數前先以同一個 client 暖機一次，排除首次請求才有的固定成本差異。
/// </summary>
public class OrderDisplayQueryCountTests : IClassFixture<QueryCountingWebApplicationFactory>
{
    // spec 列出的明細請求資料表。身分驗證等固定查詢表以暖機請求實測為零張（JWT 驗證不查資料庫），故白名單不另加；
    // 日後若驗證流程開始查表，此測試會失敗並列出該表，屆時再明確加入，不以「訂單無關」模糊放行。
    private static readonly string[] BuyerDetailTables = ["Orders", "OrderItems", "Tickets", "Events", "TicketTypes", "EventSeats", "Seats"];

    private readonly QueryCountingWebApplicationFactory _factory;

    public OrderDisplayQueryCountTests(QueryCountingWebApplicationFactory factory)
    {
        _factory = factory;
    }

    public enum ItemComposition
    {
        SeatOnly,
        CountOnly,
        Mixed,
    }

    // [BOQ-DETAIL-DISPLAY-004]
    [Theory]
    [InlineData(ItemComposition.SeatOnly)]
    [InlineData(ItemComposition.CountOnly)]
    [InlineData(ItemComposition.Mixed)]
    public async Task GetMyOrderDetail_WithOneVersusFiveItems_IssuesSameQueriesPerTable(ItemComposition composition)
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var seededEvent = await OrderDisplayTestData.SeedEventAsync(_factory, organizerClient, "Query Count Event", seatCount: 6, countTicketTypeCount: 5);
        var (buyerClient, _) = await OrderDisplayTestData.CreateBuyerClientAsync(_factory);
        // 混合組成至少要 2 個項目（1 座位＋1 計數），無法做成 1 個項目，以 2 對 5 比較。
        var (smallSeatCount, smallCountCount, largeSeatCount, largeCountCount) = composition switch
        {
            ItemComposition.SeatOnly => (1, 0, 5, 0),
            ItemComposition.CountOnly => (0, 1, 0, 5),
            _ => (1, 1, 3, 2),
        };
        var smallOrderId = await OrderDisplayTestData.PlaceOrderAsync(buyerClient, seededEvent, smallSeatCount, smallCountCount, firstSeatIndex: 0);
        var largeOrderId = await OrderDisplayTestData.PlaceOrderAsync(buyerClient, seededEvent, largeSeatCount, largeCountCount, firstSeatIndex: 1);

        (await buyerClient.GetAsync($"/api/orders/{smallOrderId}")).EnsureSuccessStatusCode();
        var smallCounts = await GetQueryCountsByTableAsync(buyerClient, $"/api/orders/{smallOrderId}");
        var largeCounts = await GetQueryCountsByTableAsync(buyerClient, $"/api/orders/{largeOrderId}");

        largeCounts.Should().BeEquivalentTo(smallCounts);
        smallCounts.Keys.Should().BeSubsetOf(BuyerDetailTables,
            "SeatMaps 出現代表誤用 ISeatMapRepository.GetByIdAsync 載入了整張座位圖");
        smallCounts.GetValueOrDefault("Orders").Should().Be(1);
        smallCounts.GetValueOrDefault("Tickets").Should().Be(1);
        smallCounts.GetValueOrDefault("Events").Should().Be(1);
        smallCounts.GetValueOrDefault("TicketTypes").Should().Be(1);
        var expectedSeatQueries = composition == ItemComposition.CountOnly ? 0 : 1;
        smallCounts.GetValueOrDefault("EventSeats").Should().Be(expectedSeatQueries);
        smallCounts.GetValueOrDefault("Seats").Should().Be(expectedSeatQueries);
    }

    // [BOQ-LIST-TITLE-003]
    [Fact]
    public async Task GetMyOrders_WithOneVersusThreeEvents_IssuesSameQueriesPerTable()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var (singleEventBuyer, _) = await OrderDisplayTestData.CreateBuyerClientAsync(_factory);
        var (multiEventBuyer, _) = await OrderDisplayTestData.CreateBuyerClientAsync(_factory);
        for (var i = 0; i < 3; i++)
        {
            var seededEvent = await OrderDisplayTestData.SeedEventAsync(_factory, organizerClient, $"List Event {i}", seatCount: 2, countTicketTypeCount: 0);
            if (i == 0)
            {
                await OrderDisplayTestData.PlaceOrderAsync(singleEventBuyer, seededEvent, seatItemCount: 1, countItemCount: 0, firstSeatIndex: 1);
            }

            await OrderDisplayTestData.PlaceOrderAsync(multiEventBuyer, seededEvent, seatItemCount: 1, countItemCount: 0);
        }

        (await singleEventBuyer.GetAsync("/api/orders")).EnsureSuccessStatusCode();
        (await multiEventBuyer.GetAsync("/api/orders")).EnsureSuccessStatusCode();
        var singleEventCounts = await GetQueryCountsByTableAsync(singleEventBuyer, "/api/orders");
        var multiEventCounts = await GetQueryCountsByTableAsync(multiEventBuyer, "/api/orders");

        multiEventCounts.Should().BeEquivalentTo(singleEventCounts);
        singleEventCounts.GetValueOrDefault("Events").Should().Be(1);
    }

    // [ORD-LIST-004] 單元層的 Fake 無法區分單次 Contains 與逐筆查詢，此保證只在整合層驗證。
    [Fact]
    public async Task GetAdminOrders_WithOneVersusThreeBuyers_IssuesSameQueriesPerTable()
    {
        var (singleBuyerOrganizer, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var (multiBuyerOrganizer, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var singleBuyerEvent = await OrderDisplayTestData.SeedEventAsync(_factory, singleBuyerOrganizer, "Admin List A", seatCount: 1, countTicketTypeCount: 0);
        var multiBuyerEvent = await OrderDisplayTestData.SeedEventAsync(_factory, multiBuyerOrganizer, "Admin List B", seatCount: 3, countTicketTypeCount: 0);
        var (singleBuyer, _) = await OrderDisplayTestData.CreateBuyerClientAsync(_factory);
        await OrderDisplayTestData.PlaceOrderAsync(singleBuyer, singleBuyerEvent, seatItemCount: 1, countItemCount: 0);
        for (var i = 0; i < 3; i++)
        {
            var (buyer, _) = await OrderDisplayTestData.CreateBuyerClientAsync(_factory, $"Buyer {i}");
            await OrderDisplayTestData.PlaceOrderAsync(buyer, multiBuyerEvent, seatItemCount: 1, countItemCount: 0, firstSeatIndex: i);
        }

        (await singleBuyerOrganizer.GetAsync("/api/admin/orders")).EnsureSuccessStatusCode();
        (await multiBuyerOrganizer.GetAsync("/api/admin/orders")).EnsureSuccessStatusCode();
        var singleBuyerCounts = await GetQueryCountsByTableAsync(singleBuyerOrganizer, "/api/admin/orders");
        var multiBuyerCounts = await GetQueryCountsByTableAsync(multiBuyerOrganizer, "/api/admin/orders");

        multiBuyerCounts.Should().BeEquivalentTo(singleBuyerCounts);
        // 正向錨點（以暖機後實測寫死）：買家名稱確實以單次 Members 查詢取得。只比兩次相等的話，
        // 若買家名稱查詢不經資料庫（例如日後加快取）兩邊都是 0，測試仍會空洞通過。
        singleBuyerCounts.GetValueOrDefault("Members").Should().Be(1);
        singleBuyerCounts.GetValueOrDefault("Orders").Should().Be(1);
    }

    private async Task<IReadOnlyDictionary<string, int>> GetQueryCountsByTableAsync(HttpClient client, string url)
    {
        var tag = Guid.NewGuid().ToString("N");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(QueryCountingInterceptor.TagHeaderName, tag);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.QueryCounter.GetCommandTexts(tag).Should().NotBeEmpty("interceptor 必須確實攔截到被測請求的查詢，否則比較兩個空集合永遠會通過");
        return _factory.QueryCounter.GetQueryCountsByTable(tag);
    }
}
