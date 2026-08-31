using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Application.Events.CreateEvent;
using ProjectC.Application.Events.GetEventSeats;
using ProjectC.Application.Orders.PlaceOrder;
using ProjectC.Application.Tickets.CreateTicketType;
using ProjectC.Application.Venues.CreateSeatMap;
using ProjectC.Application.Venues.CreateVenue;
using ProjectC.Infrastructure.Persistence;
using ProjectC.WebApi.BackgroundServices;
using ProjectC.WebApi.Tests.TestSupport;
using Serilog.Events;

namespace ProjectC.WebApi.Tests.Observability;

public class BackgroundServiceTraceIdTests : IClassFixture<ObservabilityWebApplicationFactory>
{
    private readonly ObservabilityWebApplicationFactory _factory;

    public BackgroundServiceTraceIdTests(ObservabilityWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.LogSink.Clear();
    }

    // 刻意用 DI 解析出的真實 ILogger（非 NullLogger）——本測試要斷言實際輸出的日誌內容，
    // 沿用既有 ExpiredOrderCleanupServiceTests 用 NullLogger 的寫法會讓所有日誌被丟棄。
    private ExpiredOrderCleanupService CreateService()
        => new(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            _factory.Services.GetRequiredService<IDateTimeProvider>(),
            new OrderCleanupOptions(),
            _factory.Services.GetRequiredService<ILogger<ExpiredOrderCleanupService>>());

    private static async Task<Guid> ReadCreatedIdAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreatedResponse>();
        return created!.Id;
    }

    /// <summary>建立一筆逾時的 Pending 訂單，並讓座位陷入「已由本訂單售出但訂單仍是 Pending」的不一致
    /// 狀態（比照 ExpiredOrderCleanupServiceTests 的既有手法），確保 CleanupOnceAsync 處理它時一定會
    /// 走進 LogWarning 分支，才能在同一輪次內拿到可觀察的多筆日誌。</summary>
    private async Task<Guid> SeedExpiredPendingOrderThatWillFailToCancelAsync()
    {
        // AuthTestHelper.CreateAuthenticatedAdminClientAsync 只接受 CustomWebApplicationFactory，
        // 這裡改用同一支 helper 底層用的三個泛用方法自己組（Register → PromoteToAdmin → Login），
        // 對任何 WebApplicationFactory<Program> 都適用，不需要擴大既有 helper 的參數型別。
        var adminEmail = AuthTestHelper.NewEmail();
        await AuthTestHelper.RegisterAsync(_factory.CreateClient(), adminEmail);
        await AuthTestHelper.PromoteToAdminAsync(_factory.Services, adminEmail);
        var adminTokens = await AuthTestHelper.LoginAsync(_factory.CreateClient(), adminEmail);
        var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminTokens.AccessToken);

        var venueResponse = await adminClient.PostAsJsonAsync("/api/admin/venues", new CreateVenueRequest("TraceId Test Venue"));
        var venueId = await ReadCreatedIdAsync(venueResponse);
        var seatMapResponse = await adminClient.PostAsJsonAsync(
            $"/api/admin/venues/{venueId}/seat-maps", new CreateSeatMapRequest([new SeatRequest("A", "1")]));
        var seatMapId = await ReadCreatedIdAsync(seatMapResponse);
        var eventResponse = await adminClient.PostAsJsonAsync(
            "/api/admin/events", new CreateEventRequest("TraceId Test Event", DateTime.UtcNow.AddDays(30), venueId, seatMapId));
        var eventId = await ReadCreatedIdAsync(eventResponse);
        var ticketTypeResponse = await adminClient.PostAsJsonAsync(
            $"/api/admin/events/{eventId}/ticket-types", new CreateTicketTypeRequest("A", 500m));
        var ticketTypeId = await ReadCreatedIdAsync(ticketTypeResponse);

        var publicClient = _factory.CreateClient();
        var seatsResponse = await publicClient.GetAsync($"/api/events/{eventId}/seats");
        var seats = await seatsResponse.Content.ReadFromJsonAsync<List<EventSeatDto>>();
        var eventSeatId = seats!.Single().EventSeatId;

        var buyerClient = _factory.CreateClient();
        var tokens = await AuthTestHelper.RegisterAndLoginAsync(buyerClient);
        buyerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var placeResponse = await buyerClient.PostAsJsonAsync(
            "/api/orders", new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeatId, ticketTypeId)]));
        var orderId = await ReadCreatedIdAsync(placeResponse);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var order = await dbContext.Orders.SingleAsync(o => o.Id == orderId);
        dbContext.Entry(order).Property(o => o.HeldUntilUtc).CurrentValue = DateTime.UtcNow.AddMinutes(-1);
        var eventSeat = await dbContext.EventSeats.SingleAsync(es => es.Id == eventSeatId);
        dbContext.Entry(eventSeat).Property("_soldByOrderId").CurrentValue = orderId;
        await dbContext.SaveChangesAsync();

        return orderId;
    }

    // 對應 AC: OBS-BACKGROUND-CYCLE-TRACE
    [Fact]
    public async Task SingleCycle_MultipleItems_ShareSameTraceId_DifferentFromNextCycle()
    {
        await SeedExpiredPendingOrderThatWillFailToCancelAsync();
        await SeedExpiredPendingOrderThatWillFailToCancelAsync();

        var service = CreateService();

        _factory.LogSink.Clear();
        await service.CleanupOnceAsync(CancellationToken.None);
        var firstCycleTraceIds = ExtractCleanupWarningTraceIds(_factory.LogSink.Events);

        firstCycleTraceIds.Should().HaveCount(2, "兩筆都會失敗，第一輪應該有兩筆 LogWarning");
        firstCycleTraceIds.Distinct().Should().ContainSingle("同一輪次的日誌應該共用同一個 TraceId");

        await SeedExpiredPendingOrderThatWillFailToCancelAsync();

        _factory.LogSink.Clear();
        await service.CleanupOnceAsync(CancellationToken.None);
        var secondCycleTraceIds = ExtractCleanupWarningTraceIds(_factory.LogSink.Events);

        secondCycleTraceIds.Should().NotBeEmpty();
        secondCycleTraceIds.Distinct().Should().ContainSingle("第二輪次的日誌也應該共用同一個 TraceId");
        secondCycleTraceIds.Distinct().Single().Should().NotBe(
            firstCycleTraceIds.Distinct().Single(), "不同輪次的 TraceId 不應該相同");
    }

    private static List<string> ExtractCleanupWarningTraceIds(IReadOnlyCollection<LogEvent> events)
    {
        return events
            .Where(e => e.Properties.TryGetValue("SourceContext", out var context)
                && context is ScalarValue { Value: string value }
                && value.Contains("ExpiredOrderCleanupService", StringComparison.Ordinal))
            .Where(e => e.Properties.ContainsKey("TraceId"))
            .Select(e => (string)((ScalarValue)e.Properties["TraceId"]).Value!)
            .ToList();
    }
}
