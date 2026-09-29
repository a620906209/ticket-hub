using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using ProjectC.Application.Events.CreateEvent;
using ProjectC.Application.Events.GetEventSeats;
using ProjectC.Application.Orders.PlaceOrder;
using ProjectC.Application.Tickets.RedeemTicket;
using ProjectC.Application.Venues.CreateSeatMap;
using ProjectC.Application.Venues.CreateVenue;
using ProjectC.Application.Tickets.CreateTicketType;
using ProjectC.WebApi.Tests.TestSupport;
using Serilog.Events;

namespace ProjectC.WebApi.Tests.Observability;

// 驗證既有 email-notification／ticket-issuance／ticket-redemption 能力定義的敏感資訊遮蔽規則，
// 在换成 Serilog 結構化輸出後仍然持續適用——不只檢查渲染後的訊息文字，直接檢查 LogEvent 的
// 結構化屬性本身（observability spec.md「既有能力定義的敏感資訊遮蔽規則在結構化日誌下持續適用」）。
public class SensitiveDataMaskingInStructuredPropertiesTests : IClassFixture<ObservabilityWebApplicationFactory>
{
    private readonly ObservabilityWebApplicationFactory _factory;

    public SensitiveDataMaskingInStructuredPropertiesTests(ObservabilityWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.LogSink.Clear();
    }

    private static async Task<Guid> ReadCreatedIdAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreatedResponse>();
        return created!.Id;
    }

    // 對應 AC: OBS-EMAIL-MASKED-IN-STRUCTURED-PROPERTIES
    [Fact]
    public async Task ConfirmOrder_NotificationLog_EmailPropertyIsMaskedNotRawValue()
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(_factory);
        var venueId = await ReadCreatedIdAsync(await adminClient.PostAsJsonAsync("/api/admin/venues", new CreateVenueRequest("Masking Test Venue")));
        var seatMapId = await ReadCreatedIdAsync(await adminClient.PostAsJsonAsync(
            $"/api/admin/venues/{venueId}/seat-maps", new CreateSeatMapRequest([new SeatRequest("A", "1")])));
        var eventId = await ReadCreatedIdAsync(await adminClient.PostAsJsonAsync(
            "/api/admin/events", new CreateEventRequest("Masking Test Event", DateTime.UtcNow.AddDays(30), venueId, seatMapId)));
        var ticketTypeId = await ReadCreatedIdAsync(await adminClient.PostAsJsonAsync(
            $"/api/admin/events/{eventId}/ticket-types", new CreateTicketTypeRequest("A", 500m)));

        var seats = await (await _factory.CreateClient().GetAsync($"/api/events/{eventId}/seats"))
            .Content.ReadFromJsonAsync<List<EventSeatDto>>();
        var eventSeatId = seats!.Single().EventSeatId;

        var buyerEmail = $"masking-{Guid.NewGuid():N}@example.com";
        var buyerClient = _factory.CreateClient();
        await AuthTestHelper.RegisterAsync(buyerClient, buyerEmail);
        var buyerTokens = await AuthTestHelper.LoginAsync(buyerClient, buyerEmail);
        buyerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", buyerTokens.AccessToken);

        var orderId = await ReadCreatedIdAsync(await buyerClient.PostAsJsonAsync(
            "/api/orders", new PlaceOrderRequest([new PlaceOrderSelectionRequest(eventSeatId, ticketTypeId)])));

        _factory.LogSink.Clear();
        var confirmResponse = await buyerClient.PostAsync($"/api/orders/{orderId}/confirm", null);
        confirmResponse.EnsureSuccessStatusCode();

        var notificationEvents = _factory.LogSink.Events
            .Where(e => e.Properties.ContainsKey("ToEmail"))
            .ToList();

        notificationEvents.Should().ContainSingle("確認訂單成功後應該觸發一次出票通知記錄");
        var toEmailProperty = ((ScalarValue)notificationEvents.Single().Properties["ToEmail"]).Value as string;

        toEmailProperty.Should().NotBe(buyerEmail, "結構化屬性也不能是未遮蔽的完整 Email");
        toEmailProperty.Should().MatchRegex(@"^.\*\*\*@example\.com$", "應該是 EmailMasker.Mask 的遮蔽格式");

        // 逐一檢查每一筆日誌的所有屬性（不只 ToEmail 這個已知欄位），確保沒有其他屬性意外夾帶完整 Email。
        foreach (var evt in _factory.LogSink.Events)
        {
            foreach (var property in evt.Properties.Values)
            {
                property.ToString().Should().NotContain(buyerEmail);
            }
        }
    }

    // 對應 AC: OBS-SIGNATURE-NOT-IN-STRUCTURED-PROPERTIES
    [Fact]
    public async Task Redeem_WithTamperedSignature_SignatureNeverAppearsInAnyLoggedProperty()
    {
        const string tamperedSignature = "definitely-not-a-real-signature-marker-xyz";
        // 核銷端點要求已切換 Organizer；未切換會在授權階段就 403，簽章根本進不到 Handler，這個測試會空洞通過。
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);

        var response = await organizerClient.PatchAsync(
            $"/api/admin/tickets/{Guid.NewGuid()}/redeem",
            JsonContent.Create(new RedeemTicketRequest(tamperedSignature)));

        // 400 InvalidTicketSignature 證明請求確實通過授權、簽章已進入 Handler 驗證路徑。
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        foreach (var evt in _factory.LogSink.Events)
        {
            evt.RenderMessage().Should().NotContain(tamperedSignature);
            foreach (var property in evt.Properties.Values)
            {
                property.ToString().Should().NotContain(tamperedSignature);
            }
        }
    }
}
