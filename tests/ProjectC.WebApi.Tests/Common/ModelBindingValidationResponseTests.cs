using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Common;

// validation-error-fields：框架模型繫結失敗的 400 必須與 Handler 路徑同格式（title=Validation、errors、
// traceId=TraceIdentifier、無 detail），client 才能用同一套邏輯處理兩條路徑。
// 鍵集合一律斷言「恰等於」，以防框架內部鍵（參數名、$.）外洩或空集合假通過。
public class ModelBindingValidationResponseTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string TraceIdentifierHeader = "X-Test-Trace-Identifier";
    private const string ActivityIdHeader = "X-Test-Activity-Id";
    private const string ValidFields =
        "\"startAtUtc\":\"2030-01-01T00:00:00Z\",\"venueId\":\"11111111-1111-1111-1111-111111111111\",\"seatMapId\":\"22222222-2222-2222-2222-222222222222\"";

    private readonly WebApplicationFactory<Program> _factory;

    public ModelBindingValidationResponseTests(CustomWebApplicationFactory factory)
    {
        _factory = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(
            services => services.AddTransient<IStartupFilter, RequestIdentifierCaptureStartupFilter>()));
    }

    // [AER-026]
    [Fact]
    public async Task CreateEvent_WithWrongTypedField_Returns400ValidationWithFieldKeyAndTraceId()
    {
        var body = $"{{\"title\":\"t\",{ValidFields},\"maxTicketsPerOrder\":\"abc\"}}";

        var (response, json) = await PostCreateEventAsync(body);

        await AssertValidationProblemAsync(response, json, expectedKeys: ["maxTicketsPerOrder"]);
        json.RootElement.GetProperty("errors").GetProperty("maxTicketsPerOrder").GetArrayLength().Should().BeGreaterThan(0);
    }

    // [AER-027]
    [Fact]
    public async Task CreateEvent_WithMalformedJson_Returns400ValidationWithErrors()
    {
        var (response, json) = await PostCreateEventAsync("{not json");

        await AssertValidationProblemAsync(response, json, expectedKeys: null);
        json.RootElement.GetProperty("errors").EnumerateObject().Should()
            .OnlyContain(property => property.Value.GetArrayLength() > 0);
    }

    // [AER-028]
    [Fact]
    public async Task CreateEvent_WithEmptyBody_Returns400ValidationWithEmptyKey()
    {
        var (response, json) = await PostCreateEventAsync(string.Empty);

        await AssertValidationProblemAsync(response, json, expectedKeys: [string.Empty]);
    }

    // [AER-034]
    [Fact]
    public async Task CreateEvent_WithNullLiteralBody_Returns400ValidationWithEmptyKey()
    {
        var (response, json) = await PostCreateEventAsync("null");

        await AssertValidationProblemAsync(response, json, expectedKeys: [string.Empty]);
    }

    // [AER-033] 缺漏與 null 觸發框架的隱含必填（模型繫結階段，無 detail）；空字串不在此列（見 AER-035）。
    [Theory]
    [InlineData("{" + ValidFields + "}")]
    [InlineData("{\"title\":null," + ValidFields + "}")]
    public async Task CreateEvent_WithMissingOrNullTitle_Returns400ValidationWithTitleKey(string body)
    {
        var (response, json) = await PostCreateEventAsync(body);

        await AssertValidationProblemAsync(response, json, expectedKeys: ["title"]);
    }

    // [AER-029]
    [Fact]
    public async Task CreateEvent_WithWrongTypedField_ErrorKeysHaveNoDollarPrefix()
    {
        var body = $"{{\"title\":\"t\",{ValidFields},\"maxTicketsPerOrder\":\"abc\"}}";

        var (_, json) = await PostCreateEventAsync(body);

        var keys = json.RootElement.GetProperty("errors").EnumerateObject().Select(property => property.Name).ToArray();
        keys.Should().NotBeEmpty();
        keys.Should().OnlyContain(key => !key.StartsWith('$'));
    }

    // [AER-029] client 送出含空白的屬性名時 System.Text.Json 回報括號路徑，鍵同樣不得帶 `$`。
    [Fact]
    public async Task CreateEvent_WithMalformedUnknownPropertyName_ErrorKeysHaveNoDollarPrefix()
    {
        var (_, json) = await PostCreateEventAsync("{\"title\":\"t\",\"x y\": tru}");

        var keys = json.RootElement.GetProperty("errors").EnumerateObject().Select(property => property.Name).ToArray();
        keys.Should().NotBeEmpty();
        keys.Should().OnlyContain(key => !key.StartsWith('$'));
    }

    private async Task<(HttpResponseMessage Response, JsonDocument Json)> PostCreateEventAsync(string body)
    {
        var (client, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var response = await client.PostAsync("/api/admin/events", new StringContent(body, Encoding.UTF8, "application/json"));
        return (response, JsonDocument.Parse(await response.Content.ReadAsStringAsync()));
    }

    private static Task AssertValidationProblemAsync(HttpResponseMessage response, JsonDocument json, string[]? expectedKeys)
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        var root = json.RootElement;
        root.GetProperty("status").GetInt32().Should().Be(400);
        root.GetProperty("title").GetString().Should().Be("Validation");
        root.TryGetProperty("detail", out _).Should().BeFalse();

        // 先確認 Activity id 與 TraceIdentifier 確實不同，否則 traceId 斷言無法區分兩者而空轉。
        var traceIdentifier = response.Headers.GetValues(TraceIdentifierHeader).Single();
        var activityId = response.Headers.GetValues(ActivityIdHeader).Single();
        activityId.Should().NotBeNullOrEmpty().And.NotBe(traceIdentifier);
        root.GetProperty("traceId").GetString().Should().Be(traceIdentifier);

        var keys = root.GetProperty("errors").EnumerateObject().Select(property => property.Name).ToArray();
        if (expectedKeys is null)
        {
            keys.Should().NotBeEmpty();
        }
        else
        {
            keys.Should().BeEquivalentTo(expectedKeys);
        }

        return Task.CompletedTask;
    }

    private sealed class RequestIdentifierCaptureStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                var traceIdentifier = context.TraceIdentifier;
                var activityId = Activity.Current?.Id ?? string.Empty;
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers[TraceIdentifierHeader] = traceIdentifier;
                    context.Response.Headers[ActivityIdHeader] = activityId;
                    return Task.CompletedTask;
                });
                await nextMiddleware();
            });
            next(app);
        };
    }
}
