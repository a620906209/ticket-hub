using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using ProjectC.Application.Tickets.RedeemTicket;
using ProjectC.Domain.Tickets;
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
