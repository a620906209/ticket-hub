using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Application.Events.CreateEvent;
using ProjectC.Application.Events.GetEventSeats;
using ProjectC.Application.Orders.PlaceOrder;
using ProjectC.Application.Tickets.CreateTicketType;
using ProjectC.Application.Venues.CreateSeatMap;
using ProjectC.Application.Venues.CreateVenue;
using ProjectC.Domain.Orders;
using ProjectC.Infrastructure.Persistence;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Startup;

// mock-service-production-guard MOCKGUARD-008：旗標只放行啟動檢查，不改變 Mock 付款的處理結果。
public class MockServicesAllowedBehaviorTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public MockServicesAllowedBehaviorTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private WebApplicationFactory<Program> CreateEnvironmentFactory(string environmentName, string? allowFlag)
        => _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environmentName);
            // api 容器的環境變數可能帶有 DevelopmentSeed／AdminBootstrap 設定，以較晚加入的來源明確清空。
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["AdminBootstrap:Email"] = string.Empty,
                    ["AdminBootstrap:Password"] = string.Empty,
                    ["DevelopmentSeed:AdminEmail"] = string.Empty,
                    ["DevelopmentSeed:AdminPassword"] = string.Empty,
                };
                if (allowFlag is not null)
                {
                    values["Mocks:AllowInProduction"] = allowFlag;
                }

                configBuilder.AddInMemoryCollection(values);
            });
        });

    private static async Task<Guid> ReadCreatedIdAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreatedResponse>();
        return created!.Id;
    }

    private static async Task<(HttpStatusCode ConfirmStatus, OrderStatus FinalStatus)> PlaceAndConfirmOrderAsync(
        WebApplicationFactory<Program> factory)
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(factory);
        var venueId = await ReadCreatedIdAsync(
            await adminClient.PostAsJsonAsync("/api/admin/venues", new CreateVenueRequest("Test Venue")));
        var seatMapId = await ReadCreatedIdAsync(await adminClient.PostAsJsonAsync(
            $"/api/admin/venues/{venueId}/seat-maps",
            new CreateSeatMapRequest([new SeatRequest("A", "1")])));
        var eventId = await ReadCreatedIdAsync(await adminClient.PostAsJsonAsync(
            "/api/admin/events",
            new CreateEventRequest("Concert", DateTime.UtcNow.AddDays(30), venueId, seatMapId)));
        var ticketTypeId = await ReadCreatedIdAsync(await adminClient.PostAsJsonAsync(
            $"/api/admin/events/{eventId}/ticket-types", new CreateTicketTypeRequest("A", 500m)));

        var buyerClient = factory.CreateClient();
        var tokens = await AuthTestHelper.RegisterAndLoginAsync(buyerClient);
        buyerClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var seats = await buyerClient.GetFromJsonAsync<List<EventSeatDto>>($"/api/events/{eventId}/seats");
        var eventSeatId = seats!.Single().EventSeatId;
        var orderId = await ReadCreatedIdAsync(await buyerClient.PostAsJsonAsync(
            "/api/orders", new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeatId, ticketTypeId)])));

        var confirmResponse = await buyerClient.PostAsync($"/api/orders/{orderId}/confirm", null);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var order = await dbContext.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        return (confirmResponse.StatusCode, order.Status);
    }

    // MOCKGUARD-008
    [Fact]
    public async Task ConfirmOrder_InProductionWithFlag_BehavesAsInDevelopment()
    {
        using var developmentFactory = CreateEnvironmentFactory("Development", allowFlag: null);
        using var productionFactory = CreateEnvironmentFactory("Production", allowFlag: "true");

        var development = await PlaceAndConfirmOrderAsync(developmentFactory);
        var production = await PlaceAndConfirmOrderAsync(productionFactory);

        development.ConfirmStatus.Should().Be(HttpStatusCode.NoContent);
        development.FinalStatus.Should().Be(OrderStatus.Paid);
        production.Should().Be(development);
    }
}
