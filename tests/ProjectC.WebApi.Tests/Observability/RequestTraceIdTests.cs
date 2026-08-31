using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using ProjectC.Application.Authentication.Login;
using ProjectC.WebApi.Tests.TestSupport;
using Serilog.Events;

namespace ProjectC.WebApi.Tests.Observability;

public class RequestTraceIdTests : IClassFixture<ObservabilityWebApplicationFactory>
{
    private readonly ObservabilityWebApplicationFactory _factory;

    public RequestTraceIdTests(ObservabilityWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.LogSink.Clear();
    }

    // 對應 AC: OBS-REQUEST-TRACE-CONSISTENT
    [Fact]
    public async Task Request_AllLoggedEvents_ShareSameTraceId_MatchingResponseProblemDetailsTraceId()
    {
        var client = _factory.CreateClient();
        var email = $"trace-{Guid.NewGuid():N}@example.com";

        // 先在隔離窗口之外把額度打滿（LoginRateLimiting:PermitLimit = 3，見
        // ObservabilityWebApplicationFactory），確保接下來「隔離觀察的那一次」請求穩定會被拒絕，
        // 不用在隔離窗口內夾雜不確定次數的暖機請求。
        for (var i = 0; i < 3; i++)
        {
            await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "WrongPassword1"));
        }

        // 清空後只送這一次請求，確保接下來收集到的 LogEvent 全部、只屬於這一次請求
        // （原本的寫法混雜了暖機請求的日誌，只斷言「至少一筆」符合，蓋不到「這次請求的其他日誌
        // 有沒有漏掉 TraceId 或帶了不同值」這個情境，實測發現）。
        _factory.LogSink.Clear();
        var rejectedResponse = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "WrongPassword1"));
        rejectedResponse.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "額度應該已經在暖機階段打滿");

        var body = await rejectedResponse.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var responseTraceId = document.RootElement.GetProperty("traceId").GetString();

        var events = _factory.LogSink.Events;
        events.Should().NotBeEmpty("這次請求應該至少產生一筆日誌（例如請求摘要日誌）");
        events.Should().OnlyContain(
            e => e.Properties.ContainsKey("TraceId"),
            "這次請求期間產生的每一筆日誌都必須帶有 TraceId 屬性，不能有漏掉的");
        events
            .Select(e => ((ScalarValue)e.Properties["TraceId"]).Value)
            .Distinct()
            .Should().ContainSingle().Which.Should().Be(responseTraceId, "所有日誌的 TraceId 都必須是同一個值，且與回應的 traceId 一致");
    }

    // 對應 AC: OBS-REQUEST-TRACE-UNIQUE
    [Fact]
    public async Task TwoDifferentRequests_HaveDifferentTraceIds()
    {
        var client = _factory.CreateClient();

        _factory.LogSink.Clear();
        (await client.GetAsync("/api/events")).EnsureSuccessStatusCode();
        var firstTraceIds = ExtractTraceIds(_factory.LogSink.Events);

        _factory.LogSink.Clear();
        (await client.GetAsync("/api/events")).EnsureSuccessStatusCode();
        var secondTraceIds = ExtractTraceIds(_factory.LogSink.Events);

        firstTraceIds.Should().NotBeEmpty();
        secondTraceIds.Should().NotBeEmpty();
        firstTraceIds.Intersect(secondTraceIds).Should().BeEmpty("兩次不同請求的 TraceId 不應該重複");
    }

    private static HashSet<string> ExtractTraceIds(IReadOnlyCollection<LogEvent> events)
    {
        return events
            .Where(e => e.Properties.ContainsKey("TraceId"))
            .Select(e => ((ScalarValue)e.Properties["TraceId"]).Value)
            .OfType<string>()
            .ToHashSet();
    }
}
