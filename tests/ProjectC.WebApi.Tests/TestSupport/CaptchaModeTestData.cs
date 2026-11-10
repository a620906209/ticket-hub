using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Application.Events.CreateEvent;
using ProjectC.Application.Venues.CreateSeatMap;
using ProjectC.Application.Venues.CreateVenue;
using StackExchange.Redis;

namespace ProjectC.WebApi.Tests.TestSupport;

// 驗證碼模式整合測試的共用前置資料：供 PurchaseQueueCaptchaTests 與各驗證碼模式測試共用。
public static class CaptchaModeTestData
{
    public const string WrongAnswer = "WRONG";

    private static async Task<Guid> ReadCreatedIdAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreatedResponse>();
        return created!.Id;
    }

    public static async Task<Guid> SeedQueueModeEnabledEventAsync(HttpClient adminClient)
    {
        var venueResponse = await adminClient.PostAsJsonAsync("/api/admin/venues", new CreateVenueRequest("Captcha Mode Test Venue"));
        var venueId = await ReadCreatedIdAsync(venueResponse);
        var seatMapResponse = await adminClient.PostAsJsonAsync(
            $"/api/admin/venues/{venueId}/seat-maps", new CreateSeatMapRequest([new SeatRequest("A", "1")]));
        var seatMapId = await ReadCreatedIdAsync(seatMapResponse);
        var eventResponse = await adminClient.PostAsJsonAsync(
            "/api/admin/events", new CreateEventRequest("Captcha Mode Test Event", DateTime.UtcNow.AddDays(30), venueId, seatMapId));
        var eventId = await ReadCreatedIdAsync(eventResponse);

        var patchResponse = await adminClient.PatchAsJsonAsync($"/api/admin/events/{eventId}/queue-mode", new { enabled = true });
        patchResponse.EnsureSuccessStatusCode();

        return eventId;
    }

    // 以 setupFactory（通過核對的替身）註冊並登入，回傳已帶 Bearer Token 的會員 client。
    public static async Task<HttpClient> CreateAuthenticatedMemberClientAsync(WebApplicationFactory<Program> setupFactory, string email)
    {
        var client = setupFactory.CreateClient();
        var tokens = await AuthTestHelper.RegisterAndLoginAsync(client, email);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    // 與 RedisCaptchaService 相同的儲存格式：captcha:{token} → SHA256(大寫答案) 的十六進位字串。
    public static async Task SeedCaptchaAsync(IServiceProvider services, string token, string answer)
    {
        var multiplexer = services.GetRequiredService<IConnectionMultiplexer>();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(answer.ToUpperInvariant())));
        await multiplexer.GetDatabase().StringSetAsync($"captcha:{token}", hash, TimeSpan.FromSeconds(120));
    }

    public static async Task<bool> CaptchaKeyExistsAsync(IServiceProvider services, string token)
    {
        var multiplexer = services.GetRequiredService<IConnectionMultiplexer>();
        return await multiplexer.GetDatabase().KeyExistsAsync($"captcha:{token}");
    }
}
