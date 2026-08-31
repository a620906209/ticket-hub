using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using ProjectC.WebApi.Tests.TestSupport;
using Serilog.Events;

namespace ProjectC.WebApi.Tests.Observability;

// UseSerilogRequestLogging() 的預設請求摘要日誌只記錄方法/路徑/狀態碼/耗時，不含 headers 或 body
// （見 Program.cs 1.4：刻意不客製化 EnrichDiagnosticContext）。以下測試鎖住這個預設行為，
// 避免未來有人不小心加了 headers/body 進摘要日誌卻沒注意到違反規格。
public class SensitiveDataNotLoggedTests : IClassFixture<ObservabilityWebApplicationFactory>
{
    private readonly ObservabilityWebApplicationFactory _factory;

    public SensitiveDataNotLoggedTests(ObservabilityWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.LogSink.Clear();
    }

    // 對應 AC: OBS-REQUEST-LOG-NO-AUTH-HEADER
    [Fact]
    public async Task RequestWithAuthorizationHeader_TokenNotPresentInAnyLoggedEvent()
    {
        const string token = "super-secret-bearer-token-should-never-appear-in-logs";
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await client.GetAsync("/api/events");

        AllLoggedText(_factory.LogSink.Events).Should().NotContain(token);
    }

    // 對應 AC: OBS-REQUEST-LOG-NO-COOKIE
    [Fact]
    public async Task RequestWithCookie_CookieValueNotPresentInAnyLoggedEvent()
    {
        const string cookieValue = "super-secret-session-cookie-should-never-appear-in-logs";
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"session={cookieValue}");

        await client.GetAsync("/api/events");

        AllLoggedText(_factory.LogSink.Events).Should().NotContain(cookieValue);
    }

    // 對應 AC: OBS-REQUEST-LOG-NO-BODY
    [Fact]
    public async Task RequestWithJsonBody_BodyContentNotPresentInAnyLoggedEvent()
    {
        const string bodyMarker = "super-secret-request-body-marker-should-never-appear-in-logs";
        var client = _factory.CreateClient();

        await client.PostAsJsonAsync("/api/auth/login", new { email = bodyMarker, password = "WrongPassword1" });

        AllLoggedText(_factory.LogSink.Events).Should().NotContain(bodyMarker);
    }

    private static string AllLoggedText(IReadOnlyCollection<LogEvent> events)
    {
        var builder = new StringBuilder();
        foreach (var evt in events)
        {
            builder.AppendLine(evt.RenderMessage());
            foreach (var property in evt.Properties.Values)
            {
                builder.AppendLine(property.ToString());
            }
        }

        return builder.ToString();
    }
}
