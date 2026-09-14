using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Captcha;

// CAPTCHA-GEN-001／002：GET /api/captcha 端點層級驗證，真實 Redis、真實 RedisCaptchaService
// （補足 GetCaptchaHandlerTests 僅測 Handler 呼叫轉發的缺口）。
public class CaptchaComponentTests : IClassFixture<CaptchaComponentTestWebApplicationFactory>
{
    private sealed record CaptchaResponse(string Token, string ImageBase64);

    private readonly CaptchaComponentTestWebApplicationFactory _factory;

    public CaptchaComponentTests(CaptchaComponentTestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Get_ReturnsTokenAndBase64PngWithoutPlainTextAnswer()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/captcha");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rawJson = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<CaptchaResponse>(rawJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        body!.Token.Should().NotBeNullOrWhiteSpace();
        body.ImageBase64.Should().NotBeNullOrWhiteSpace();

        var imageBytes = Convert.FromBase64String(body.ImageBase64);
        imageBytes.Should().NotBeEmpty();
        // PNG 檔頭魔數，證明回應內容確實是圖片位元組，不是隨便一段 base64 字串。
        imageBytes.Take(8).Should().Equal(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);

        // 反序列化成強型別 DTO 時，System.Text.Json 預設會默默忽略未宣告的多餘欄位，不會讓測試失敗
        // ——直接核對原始 JSON 的實際欄位集合，才能真正證明回應不含答案明文或其他未預期欄位。
        using var document = JsonDocument.Parse(rawJson);
        var propertyNames = document.RootElement.EnumerateObject().Select(p => p.Name);
        propertyNames.Should().BeEquivalentTo(["token", "imageBase64"], "回應 MUST 只包含 token 與 imageBase64，不得夾帶答案明文或其他欄位");
    }

    [Fact]
    public async Task Get_CalledTwice_ReturnsTwoDifferentTokens()
    {
        using var client = _factory.CreateClient();

        var firstResponse = await client.GetAsync("/api/captcha");
        var secondResponse = await client.GetAsync("/api/captcha");

        var first = await firstResponse.Content.ReadFromJsonAsync<CaptchaResponse>();
        var second = await secondResponse.Content.ReadFromJsonAsync<CaptchaResponse>();

        first!.Token.Should().NotBe(second!.Token);
    }
}
