using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProjectC.Application.Tickets.RedeemTicket;
using ProjectC.Domain.Tickets;
using ProjectC.Infrastructure.Persistence;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Admin;

// 歸屬核對需要的上游資料查無代表資料毀損，MUST 大聲失敗（500），不得當成 404 或放行
// （order-report-redemption-organizer-scoping design.md Decision 1）。
public class OrganizerScopingDataCorruptionTests : IClassFixture<OrganizerScopingFaultInjectionWebApplicationFactory>
{
    private readonly OrganizerScopingFaultInjectionWebApplicationFactory _factory;

    public OrganizerScopingDataCorruptionTests(OrganizerScopingFaultInjectionWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // 證明 500 來自 GlobalExceptionHandler（Handler 拋出例外），而不是其他路徑回傳的 500。
    private static async Task<string> AssertGlobalExceptionProblemDetailsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("title").GetString().Should().Be("An unexpected error occurred.");
        document.RootElement.GetProperty("status").GetInt32().Should().Be(500);
        document.RootElement.TryGetProperty("traceId", out _).Should().BeTrue();
        return body;
    }

    // [ORD-DETAIL-004]
    [Fact]
    public async Task GetOrderById_WhenOrderEventMissing_Returns500WithoutOrderData()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var seeded = await OrganizerScopedTestData.SeedPendingOrderAsync(_factory, organizerClient);
        _factory.MissingEventIds.TryAdd(seeded.EventId, 0);

        var response = await organizerClient.GetAsync($"/api/admin/orders/{seeded.OrderId}");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var body = await AssertGlobalExceptionProblemDetailsAsync(response);
        body.Should().NotContain("items").And.NotContain("buyerId");
    }

    // [BOQ-LIST-TITLE-002]
    [Fact]
    public async Task GetMyOrders_WhenOrderEventMissing_Returns500WithoutOrderData()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var seeded = await OrganizerScopedTestData.SeedPendingOrderAsync(_factory, organizerClient);
        _factory.MissingEventIds.TryAdd(seeded.EventId, 0);

        var response = await seeded.BuyerClient.GetAsync("/api/orders");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var body = await AssertGlobalExceptionProblemDetailsAsync(response);
        // 只斷言 500 無法區分「Handler 的缺失檢查」與「字典取值丟 KeyNotFoundException」，須驗 log 內的例外型別與 Id。
        var exception = AssertSingleLoggedDataInconsistency(body, seeded.OrderId, seeded.EventId);
        body.Should().NotContain(seeded.OrderId.ToString()).And.NotContain("eventTitle")
            .And.NotContain(seeded.EventId.ToString())
            .And.NotContain(exception.Message);
    }

    public enum OrderDetailCorruption
    {
        MissingEvent,
        MissingEventSeat,
        MissingTicketType,
        MissingSeat,
    }

    // [BOQ-DETAIL-DISPLAY-003] 大聲失敗（500 經全域例外處理）、可定位（log 例外帶 Id 與 TraceId）、不外洩（回應不含 Id 與例外訊息）三者同時成立。
    [Theory]
    [InlineData(OrderDetailCorruption.MissingEvent)]
    [InlineData(OrderDetailCorruption.MissingEventSeat)]
    [InlineData(OrderDetailCorruption.MissingTicketType)]
    [InlineData(OrderDetailCorruption.MissingSeat)]
    public async Task GetMyOrderDetail_WhenRelatedDataMissing_Returns500AndLogsIdsWithoutLeaking(OrderDetailCorruption corruption)
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var seededEvent = await OrderDisplayTestData.SeedEventAsync(_factory, organizerClient, "Corruption Event", seatCount: 1, countTicketTypeCount: 1);
        var (buyerClient, _) = await OrderDisplayTestData.CreateBuyerClientAsync(_factory);
        var orderId = await OrderDisplayTestData.PlaceOrderAsync(buyerClient, seededEvent, seatItemCount: 1, countItemCount: 1);
        var injectedId = corruption switch
        {
            OrderDetailCorruption.MissingEvent => seededEvent.EventId,
            OrderDetailCorruption.MissingEventSeat => seededEvent.Seats[0].EventSeatId,
            OrderDetailCorruption.MissingTicketType => seededEvent.CountTicketTypeIds[0],
            _ => seededEvent.Seats[0].SeatId,
        };
        var injectionTarget = corruption switch
        {
            OrderDetailCorruption.MissingEvent => _factory.MissingEventIds,
            OrderDetailCorruption.MissingEventSeat => _factory.MissingEventSeatIds,
            OrderDetailCorruption.MissingTicketType => _factory.MissingTicketTypeIds,
            _ => _factory.MissingSeatIds,
        };
        injectionTarget.TryAdd(injectedId, 0);

        var response = await buyerClient.GetAsync($"/api/orders/{orderId}");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var body = await AssertGlobalExceptionProblemDetailsAsync(response);
        var exception = AssertSingleLoggedDataInconsistency(body, orderId, injectedId);
        body.Should().NotContain("items").And.NotContain("eventTitle")
            .And.NotContain(injectedId.ToString())
            .And.NotContain(orderId.ToString())
            .And.NotContain(exception.Message);
    }

    // [ORD-LIST-003] 單筆損毀讓整個列表 500，不回傳部分列表（design.md 決策 2）。
    [Fact]
    public async Task GetAdminOrders_WhenOneBuyerMissing_Returns500AndLogsIdsWithoutLeaking()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var seededEvent = await OrderDisplayTestData.SeedEventAsync(_factory, organizerClient, "Buyer Corruption Event", seatCount: 2, countTicketTypeCount: 0);
        var (intactBuyer, _) = await OrderDisplayTestData.CreateBuyerClientAsync(_factory, "Intact Buyer");
        var (orphanBuyer, _) = await OrderDisplayTestData.CreateBuyerClientAsync(_factory, "Orphan Buyer");
        var intactOrderId = await OrderDisplayTestData.PlaceOrderAsync(intactBuyer, seededEvent, seatItemCount: 1, countItemCount: 0, firstSeatIndex: 0);
        var orphanOrderId = await OrderDisplayTestData.PlaceOrderAsync(orphanBuyer, seededEvent, seatItemCount: 1, countItemCount: 0, firstSeatIndex: 1);
        var orphanBuyerId = await ReadBuyerIdAsync(orphanOrderId);
        _factory.MissingBuyerIds.TryAdd(orphanBuyerId, 0);

        var response = await organizerClient.GetAsync("/api/admin/orders");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var body = await AssertGlobalExceptionProblemDetailsAsync(response);
        var exception = AssertSingleLoggedDataInconsistency(body, orphanOrderId, orphanBuyerId);
        body.Should().NotContain("buyerDisplayName").And.NotContain("buyerId")
            .And.NotContain(intactOrderId.ToString())
            .And.NotContain(orphanOrderId.ToString())
            .And.NotContain(orphanBuyerId.ToString())
            .And.NotContain(exception.Message);
    }

    // 以回應的 traceId 篩出該請求的 log（factory 在整個測試類別共用同一個 logger）。
    private InvalidOperationException AssertSingleLoggedDataInconsistency(string problemDetailsBody, Guid orderId, Guid missingId)
    {
        using var document = JsonDocument.Parse(problemDetailsBody);
        var traceId = document.RootElement.GetProperty("traceId").GetString()!;
        var entry = _factory.ExceptionHandlerLogger.Entries.Should()
            .ContainSingle(e => e.Message.Contains(traceId), "每個失敗請求 MUST 恰好記錄一筆帶 TraceId 的 log").Subject;
        entry.Level.Should().Be(LogLevel.Error);
        var exception = entry.Exception.Should().BeOfType<InvalidOperationException>().Subject;
        exception.Message.Should().Contain(orderId.ToString()).And.Contain(missingId.ToString());
        return exception;
    }

    private async Task<Guid> ReadBuyerIdAsync(Guid orderId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await dbContext.Orders.AsNoTracking().Where(o => o.Id == orderId).Select(o => o.BuyerId).SingleAsync();
    }

    // [RDM-AUTHZ-007]
    [Fact]
    public async Task Redeem_WhenOrganizerLookupReturnsNull_Returns500AndTicketUnchanged()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var ticketId = await OrganizerScopedTestData.SeedIssuedTicketAsync(_factory, organizerClient);
        var ticketBefore = await OrganizerScopedTestData.ReadTicketAsync(_factory, ticketId);
        _factory.UnresolvableOrderItemIds.TryAdd(ticketBefore.OrderItemId, 0);

        var response = await OrganizerScopedTestData.RedeemAsync(organizerClient, ticketId, null);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        await AssertGlobalExceptionProblemDetailsAsync(response);
        var ticketAfter = await OrganizerScopedTestData.ReadTicketAsync(_factory, ticketId);
        ticketAfter.Status.Should().Be(TicketStatus.Issued);
        ticketAfter.RedeemedAtUtc.Should().BeNull();

        // 例外路徑的交易內沒有任何修改，「票券不變」證明不了 rollback 與釋放列鎖；解除注入後同一張票
        // 必須能在時限內核銷成功——若失敗路徑遺留未結束的交易或 FOR UPDATE 鎖，這次請求會卡住逾時。
        _factory.UnresolvableOrderItemIds.TryRemove(ticketBefore.OrderItemId, out _);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var retryResponse = await organizerClient.PatchAsJsonAsync(
            $"/api/admin/tickets/{ticketId}/redeem", new RedeemTicketRequest(null), timeout.Token);

        retryResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await OrganizerScopedTestData.ReadTicketAsync(_factory, ticketId)).Status.Should().Be(TicketStatus.Redeemed);
    }
}
