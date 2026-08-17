using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Application.Events.CreateEvent;
using ProjectC.Application.Events.GetEventSeats;
using ProjectC.Application.Orders.PlaceOrder;
using ProjectC.Application.Tickets.CreateTicketType;
using ProjectC.Application.Venues.CreateSeatMap;
using ProjectC.Application.Venues.CreateVenue;
using ProjectC.Domain.Orders;
using ProjectC.Infrastructure.Persistence;
using ProjectC.WebApi.BackgroundServices;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.BackgroundServices;

public class ExpiredOrderCleanupServiceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ExpiredOrderCleanupServiceTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private ExpiredOrderCleanupService CreateService()
        => new(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            _factory.Services.GetRequiredService<IDateTimeProvider>(),
            new OrderCleanupOptions(),
            NullLogger<ExpiredOrderCleanupService>.Instance);

    private static async Task<Guid> ReadCreatedIdAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreatedResponse>();
        return created!.Id;
    }

    /// <summary>透過既有 Admin/買家 API 建立一筆真的 Pending 訂單，再直接把 HeldUntilUtc 改到過去，模擬逾時
    /// （比照 AuthTestHelper.PromoteToAdminAsync 直接改寫私有欄位的既有手法）。</summary>
    private async Task<Guid> SeedExpiredPendingOrderAsync()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        var venueResponse = await adminClient.PostAsJsonAsync("/api/admin/venues", new CreateVenueRequest("Cleanup Test Venue"));
        var venueId = await ReadCreatedIdAsync(venueResponse);
        var seatMapResponse = await adminClient.PostAsJsonAsync(
            $"/api/admin/venues/{venueId}/seat-maps", new CreateSeatMapRequest([new SeatRequest("A", "1")]));
        var seatMapId = await ReadCreatedIdAsync(seatMapResponse);
        var eventResponse = await adminClient.PostAsJsonAsync(
            "/api/admin/events", new CreateEventRequest("Cleanup Test Event", DateTime.UtcNow.AddDays(30), venueId, seatMapId));
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
        buyerClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var placeResponse = await buyerClient.PostAsJsonAsync(
            "/api/orders", new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeatId, ticketTypeId)]));
        var orderId = await ReadCreatedIdAsync(placeResponse);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var order = await dbContext.Orders.SingleAsync(o => o.Id == orderId);
        dbContext.Entry(order).Property(o => o.HeldUntilUtc).CurrentValue = DateTime.UtcNow.AddMinutes(-1);
        await dbContext.SaveChangesAsync();

        return orderId;
    }

    private async Task<OrderStatus> ReadOrderStatusFromDbAsync(Guid orderId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var order = await dbContext.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        return order.Status;
    }

    [Fact]
    public async Task CleanupOnceAsync_CancelsExpiredPendingOrdersAndReleasesSeats()
    {
        var orderIdA = await SeedExpiredPendingOrderAsync();
        var orderIdB = await SeedExpiredPendingOrderAsync();

        await CreateService().CleanupOnceAsync(CancellationToken.None);

        (await ReadOrderStatusFromDbAsync(orderIdA)).Should().Be(OrderStatus.Cancelled);
        (await ReadOrderStatusFromDbAsync(orderIdB)).Should().Be(OrderStatus.Cancelled);
    }

    [Fact]
    public async Task CleanupOnceAsync_WhenOneOrderFailsWithAResult_StillProcessesTheRest()
    {
        var orderIdA = await SeedExpiredPendingOrderAsync();
        var orderIdB = await SeedExpiredPendingOrderAsync();

        // 讓 B 的座位陷入「已由本訂單售出，但訂單自己仍是 Pending」的不一致狀態
        // （比照 CancelOrderHandlerTests.Handle_WhenSeatWasSoldByThisSameOrder_ReturnsFailureAsInconsistentState
        // 的既有手法），讓 CancelOrderHandler.Handle 對 B 回 Error.Conflict（Result.Failure，不是例外）。
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var orderB = await dbContext.Orders.Include(o => o.Items).SingleAsync(o => o.Id == orderIdB);
            var eventSeatId = orderB.Items.Single().EventSeatId;
            var eventSeat = await dbContext.EventSeats.SingleAsync(es => es.Id == eventSeatId);
            dbContext.Entry(eventSeat).Property("_soldByOrderId").CurrentValue = orderIdB;
            await dbContext.SaveChangesAsync();
        }

        await CreateService().CleanupOnceAsync(CancellationToken.None);

        (await ReadOrderStatusFromDbAsync(orderIdA)).Should().Be(OrderStatus.Cancelled);
        (await ReadOrderStatusFromDbAsync(orderIdB)).Should().Be(OrderStatus.Pending);
    }

    [Fact]
    public async Task CleanupOnceAsync_WhenTokenIsAlreadyCancelled_ThrowsAndDoesNotProcessAnyOrder()
    {
        var orderIdA = await SeedExpiredPendingOrderAsync();
        var orderIdB = await SeedExpiredPendingOrderAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => CreateService().CleanupOnceAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await ReadOrderStatusFromDbAsync(orderIdA)).Should().Be(OrderStatus.Pending);
        (await ReadOrderStatusFromDbAsync(orderIdB)).Should().Be(OrderStatus.Pending);
    }

    [Fact]
    public void TestingEnvironment_DoesNotRegisterTheRealBackgroundService()
    {
        var hostedServices = _factory.Services.GetServices<IHostedService>();

        hostedServices.Should().NotContain(service => service is ExpiredOrderCleanupService);
    }
}
