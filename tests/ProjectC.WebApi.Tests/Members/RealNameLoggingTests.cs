using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using ProjectC.WebApi.Tests.TestSupport;
using Serilog.Events;

namespace ProjectC.WebApi.Tests.Members;

/// <summary>
/// 實名與末四碼只能出現在回應本身，任何日誌（含框架、EF Core、請求日誌）都不得帶出（real-name-verification design.md 決策 5）。
/// 每個測試用自己獨特的姓名與末四碼，並檢查 factory 啟動以來的「所有」LogEvent：比只看被測請求期間更嚴格，
/// 且不受同一 factory 其他測試的日誌干擾（它們不會用到這些值）。
/// </summary>
public class RealNameLoggingTests : IClassFixture<LogCapturingWebApplicationFactory>
{
    private const string RealNameEndpoint = "/api/members/me/real-name";

    private readonly LogCapturingWebApplicationFactory _factory;

    public RealNameLoggingTests(LogCapturingWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static string RenderFully(LogEvent logEvent)
        => string.Join(
            "\n",
            new[] { logEvent.MessageTemplate.Text, logEvent.RenderMessage(), logEvent.Exception?.ToString() ?? string.Empty }
                .Concat(logEvent.Properties.Select(p => $"{p.Key}={p.Value}")));

    private void AssertNoLogContains(params string[] personalDataValues)
    {
        var renderedEvents = _factory.LogSink.Events.Select(RenderFully).ToList();
        renderedEvents.Should().NotBeEmpty("確認日誌擷取確實運作，否則「不含個資」的斷言沒有意義");
        foreach (var value in personalDataValues)
        {
            renderedEvents.Should().NotContain(rendered => rendered.Contains(value), $"日誌不得含個資「{value}」");
        }
    }

    // [RNV-LOG-001] 登記成功、重複登記、格式錯誤與讀取個人資料四條路徑都會接觸個資。
    [Fact]
    public async Task RealNameRequests_AcrossRegisterConflictValidationAndProfile_NeverLogRealNameOrLast4()
    {
        const string realName = "實名測試甲乙丙";
        const string last4 = "8642";
        var member = await RealNameTestData.CreateMemberAsync(_factory);

        var registered = await member.Client.PutAsJsonAsync(RealNameEndpoint, new { realName, nationalIdLast4 = last4 });
        var conflict = await member.Client.PutAsJsonAsync(RealNameEndpoint, new { realName, nationalIdLast4 = last4 });
        var invalid = await member.Client.PutAsJsonAsync(RealNameEndpoint, new { realName = realName + "<", nationalIdLast4 = last4 + "x" });
        var profile = await member.Client.GetAsync("/api/members/me");

        registered.StatusCode.Should().Be(HttpStatusCode.OK);
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await profile.Content.ReadAsStringAsync()).Should().Contain(realName, "確認這條路徑真的讀到了個資");
        AssertNoLogContains(realName, last4);
    }

    // [RNV-LOG-003] 閘門讀了實名也不得寫進日誌；擋下未登記者時不得把「實名狀態」當結構化屬性記錄。
    [Fact]
    public async Task RealNameGates_ForRegisteredAndUnregisteredBuyers_NeverLogRealNameData()
    {
        const string realName = "閘門日誌測試己";
        const string last4 = "9024";
        var organizerClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(_factory);
        var orderEvent = await RealNameTestData.SeedEventAsync(_factory, organizerClient, isRealNameRequired: true);
        var queueEvent = await RealNameTestData.SeedEventAsync(_factory, organizerClient, isRealNameRequired: true, isQueueModeEnabled: true);
        var registeredBuyer = await RealNameTestData.CreateRegisteredMemberAsync(_factory, realName, last4);
        var unregisteredBuyer = await RealNameTestData.CreateMemberAsync(_factory);

        (await RealNameTestData.PlaceOrderAsync(registeredBuyer.Client, orderEvent)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await RealNameTestData.JoinQueueAsync(registeredBuyer.Client, queueEvent.EventId)).StatusCode.Should().Be(HttpStatusCode.Created);
        AssertNoLogContains(realName, last4);

        _factory.LogSink.Clear();
        (await RealNameTestData.PlaceOrderAsync(unregisteredBuyer.Client, orderEvent)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await RealNameTestData.JoinQueueAsync(unregisteredBuyer.Client, queueEvent.EventId)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        _factory.LogSink.Events.Should().NotBeEmpty();
        _factory.LogSink.Events.SelectMany(e => e.Properties.Keys)
            .Should().NotContain(key => key.Contains("RealName", StringComparison.OrdinalIgnoreCase)
                || key.Contains("NationalId", StringComparison.OrdinalIgnoreCase));
    }
}
