using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Common;

// validation-error-fields：請求 Validator 失敗的 400 必須帶逐欄位 errors，且 detail 維持舊的合併字串，
// 讓既有只讀 detail 的 client 不受影響。以真實 HTTP 管線驗證 Handler → ResultExtensions 的整條路徑。
public class HandlerValidationFieldErrorsTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string ValidFields =
        "\"startAtUtc\":\"2030-01-01T00:00:00Z\",\"venueId\":\"11111111-1111-1111-1111-111111111111\",\"seatMapId\":\"22222222-2222-2222-2222-222222222222\"";

    private readonly CustomWebApplicationFactory _factory;

    public HandlerValidationFieldErrorsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // [AER-019]
    [Fact]
    public async Task CreateEvent_WithOverlongTitle_Returns400WithTitleFieldError()
    {
        var (response, json) = await PostCreateEventAsync($"{{\"title\":\"{new string('a', 201)}\",{ValidFields}}}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        json.RootElement.GetProperty("title").GetString().Should().Be("Validation");
        json.RootElement.GetProperty("traceId").GetString().Should().NotBeNullOrEmpty();
        var errors = json.RootElement.GetProperty("errors");
        errors.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(["title"]);
        errors.GetProperty("title").GetArrayLength().Should().BeGreaterThan(0);
    }

    // [AER-022]
    [Fact]
    public async Task CreateEvent_WithOverlongTitle_DetailEqualsJoinedMessages()
    {
        var (_, json) = await PostCreateEventAsync($"{{\"title\":\"{new string('a', 201)}\",{ValidFields}}}");

        var messages = json.RootElement.GetProperty("errors").GetProperty("title").EnumerateArray().Select(m => m.GetString()).ToArray();
        messages.Should().HaveCount(1);
        json.RootElement.GetProperty("detail").GetString().Should().Be(messages[0]);
        json.RootElement.GetProperty("detail").GetString().Should()
            .Be("The length of 'Title' must be 200 characters or fewer. You entered 201 characters.");
    }

    // [AER-035] 空字串通過框架的隱含必填，由 Validator 的 NotEmpty 處理，所以有 detail。
    [Fact]
    public async Task CreateEvent_WithEmptyTitle_Returns400WithTitleFieldErrorAndDetail()
    {
        var (response, json) = await PostCreateEventAsync($"{{\"title\":\"\",{ValidFields}}}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        json.RootElement.GetProperty("title").GetString().Should().Be("Validation");
        json.RootElement.GetProperty("errors").EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(["title"]);
        json.RootElement.GetProperty("errors").GetProperty("title").GetArrayLength().Should().BeGreaterThan(0);
        json.RootElement.GetProperty("detail").GetString().Should().NotBeNullOrEmpty();
        json.RootElement.GetProperty("traceId").GetString().Should().NotBeNullOrEmpty();
    }

    // [AER-032] 欄位交錯（SalesStart、SalesEnd、SalesStart）時，detail 依回報順序，errors 依欄位分組。
    [Fact]
    public async Task CreateEvent_WithInterleavedFieldFailures_DetailFollowsReportOrderAndErrorsGroupByField()
    {
        const string m1 = "SalesStartAtUtc must be a UTC time (with 'Z' designator).";
        const string m2 = "SalesEndAtUtc must not be after StartAtUtc.";
        const string m3 = "SalesStartAtUtc must be before the effective sales end time.";
        var body = "{\"title\":\"t\"," + ValidFields +
                   ",\"salesStartAtUtc\":\"2030-01-03T00:00:00\",\"salesEndAtUtc\":\"2030-01-02T00:00:00Z\"}";

        var (_, json) = await PostCreateEventAsync(body);

        json.RootElement.GetProperty("detail").GetString().Should().Be($"{m1} {m2} {m3}");
        var errors = json.RootElement.GetProperty("errors");
        errors.GetProperty("salesStartAtUtc").EnumerateArray().Select(m => m.GetString()).Should().Equal(m1, m3);
        errors.GetProperty("salesEndAtUtc").EnumerateArray().Select(m => m.GetString()).Should().Equal(m2);
    }

    private async Task<(HttpResponseMessage Response, JsonDocument Json)> PostCreateEventAsync(string body)
    {
        var (client, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var response = await client.PostAsync("/api/admin/events", new StringContent(body, Encoding.UTF8, "application/json"));
        return (response, JsonDocument.Parse(await response.Content.ReadAsStringAsync()));
    }
}
