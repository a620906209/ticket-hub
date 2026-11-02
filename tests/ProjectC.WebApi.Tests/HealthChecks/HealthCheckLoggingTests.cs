using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Application.Events.GetEvents;
using ProjectC.WebApi.Tests.TestSupport;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using static ProjectC.WebApi.Tests.HealthChecks.HealthEndpointTestSupport;

namespace ProjectC.WebApi.Tests.HealthChecks;

// 探針每幾秒一次，成功摘要留在 Information 會淹沒日誌；但失敗摘要與其他端點的等級不能跟著被降低，否則故障時查不到。
public class HealthCheckLoggingTests
{
    private const string RequestLoggingSourceContext = "Serilog.AspNetCore.RequestLoggingMiddleware";
    private const string DatabaseConnectionSourceContext = "Microsoft.EntityFrameworkCore.Database.Connection";
    private const string HealthCheckServiceSourceContext = "Microsoft.Extensions.Diagnostics.HealthChecks.DefaultHealthCheckService";
    private const string ExceptionHandlerSourceContext = "ProjectC.WebApi.ExceptionHandling.GlobalExceptionHandler";
    private const string EventListPath = "/api/events";
    private const string FailurePasswordMarker = "marker-failure-db-password";
    private const string ExceptionPasswordMarker = "marker-real-db-password";

    private static readonly TimeSpan SummaryPollDeadline = TimeSpan.FromSeconds(10);

    // HC-LOG-SUCCESS-DEBUG
    [Fact]
    public async Task HealthEndpoints_WhenSuccessfulAtDebugLevel_LogSummaryAsDebug()
    {
        await RunWithFactoryAsync(
            factory => factory.MinimumLogLevel = "Debug",
            async factory =>
            {
                var client = factory.CreateClient();
                foreach (var path in new[] { LivePath, ReadyPath })
                {
                    (await client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.OK);

                    var summary = await WaitForSummaryAsync(factory, path);
                    summary.Level.Should().Be(LogEventLevel.Debug);
                }
            });
    }

    // HC-LOG-SUCCESS-HIDDEN
    [Fact]
    public async Task HealthEndpoints_WhenSuccessfulAtDefaultLevel_LogNoSummary()
    {
        await RunWithFactoryAsync(
            _ => { },
            async factory =>
            {
                var client = factory.CreateClient();
                (await client.GetAsync(LivePath)).StatusCode.Should().Be(HttpStatusCode.OK);
                (await client.GetAsync(ReadyPath)).StatusCode.Should().Be(HttpStatusCode.OK);

                // 順序不可對調：先等到後發請求的摘要，探測摘要才不會只是尚未寫入。
                await client.GetAsync(EventListPath);
                await WaitForSummaryAsync(factory, EventListPath);

                GetSummaries(factory, LivePath).Should().BeEmpty();
                GetSummaries(factory, ReadyPath).Should().BeEmpty();
            });
    }

    // HC-LOG-FAILURE-ERROR
    [Fact]
    public async Task GetHealthReady_WhenDatabaseUnavailable_LogsSummaryAsError()
    {
        await RunWithFactoryAsync(
            factory => factory.HostConnectionString = HealthCheckWebApplicationFactory.UnreachableDatabaseConnectionString,
            async factory =>
            {
                (await factory.CreateClient().GetAsync(ReadyPath)).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

                var summary = await WaitForSummaryAsync(factory, ReadyPath);
                summary.Level.Should().Be(LogEventLevel.Error);
            });
    }

    // HC-LOG-FAILURE-EXCEPTION
    [Fact]
    public async Task HealthEndpoints_WhenHealthCheckServiceThrows_LogSummaryAsError()
    {
        await RunWithFactoryAsync(
            factory => factory.HealthCheckServiceExceptionMessage = HealthCheckWebApplicationFactory.DefaultThrownExceptionMessage,
            async factory =>
            {
                var client = factory.CreateClient();
                foreach (var path in new[] { LivePath, ReadyPath })
                {
                    (await client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.InternalServerError);

                    var summary = await WaitForSummaryAsync(factory, path);
                    summary.Level.Should().Be(LogEventLevel.Error);
                }
            });
    }

    // HC-LOG-FAILURE-NO-PASSWORD
    [Fact]
    public async Task GetHealthReady_WhenDatabaseUnavailableAtDebugLevel_LogsNoPassword()
    {
        await RunWithFactoryAsync(
            factory =>
            {
                factory.MinimumLogLevel = "Debug";
                factory.HostConnectionString = BuildUnreachableConnectionString(FailurePasswordMarker);
            },
            async factory =>
            {
                (await factory.CreateClient().GetAsync(ReadyPath)).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

                await WaitForSummaryAsync(factory, ReadyPath);
                var summary = GetSummaries(factory, ReadyPath).Should().ContainSingle().Subject;
                summary.Level.Should().Be(LogEventLevel.Error);

                var requestEvents = GetEventsWithSameTraceId(factory, summary);
                requestEvents.Should().Contain(logEvent =>
                    GetSourceContext(logEvent) == HealthCheckServiceSourceContext && logEvent.Level == LogEventLevel.Error);
                requestEvents.Should().Contain(logEvent => GetSourceContext(logEvent) == DatabaseConnectionSourceContext);

                AssertNoEventContains(factory.LogSink.Events, FailurePasswordMarker);
            });
    }

    // HC-LOG-FAILURE-NO-PASSWORD、HC-LOG-EXCEPTION-NO-PASSWORD 掃描方法本身的驗證：沒抓到屬性內的標記，上面的掃描就沒有意義。
    [Fact]
    public void AssertNoEventContains_WhenMarkerOnlyInProperty_DetectsMarker()
    {
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            new MessageTemplate("clean", Array.Empty<MessageTemplateToken>()),
            new[] { new LogEventProperty("ConnectionString", new ScalarValue($"Password={FailurePasswordMarker}")) });

        var assert = () => AssertNoEventContains(new[] { logEvent }, FailurePasswordMarker);

        assert.Should().Throw<Exception>();
    }

    // HC-LOG-EXCEPTION-NO-PASSWORD
    [Fact]
    public async Task HealthEndpoints_WhenHealthCheckServiceThrowsAtDebugLevel_LogExceptionWithoutPassword()
    {
        await RunWithFactoryAsync(
            factory =>
            {
                factory.MinimumLogLevel = "Debug";
                factory.HostConnectionString = BuildUnreachableConnectionString(ExceptionPasswordMarker);
                factory.HealthCheckServiceExceptionMessage = HealthCheckWebApplicationFactory.DefaultThrownExceptionMessage;
            },
            async factory =>
            {
                var client = factory.CreateClient();
                var traceIds = new List<string>();
                foreach (var path in new[] { LivePath, ReadyPath })
                {
                    var response = await client.GetAsync(path);
                    response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

                    await WaitForSummaryAsync(factory, path);
                    var summary = GetSummaries(factory, path).Should().ContainSingle().Subject;
                    summary.Level.Should().Be(LogEventLevel.Error);

                    var traceId = GetRequiredTraceId(summary);
                    document.RootElement.GetProperty("traceId").GetString().Should().Be(traceId);
                    traceIds.Add(traceId);

                    GetEventsWithSameTraceId(factory, summary).Should().Contain(logEvent =>
                        GetSourceContext(logEvent) == ExceptionHandlerSourceContext
                        && logEvent.Level == LogEventLevel.Error
                        && logEvent.Exception != null
                        && logEvent.Exception.ToString().Contains(HealthCheckWebApplicationFactory.ThrownExceptionMarker));
                }

                traceIds.Should().OnlyHaveUniqueItems();
                AssertNoEventContains(factory.LogSink.Events, ExceptionPasswordMarker);
            });
    }

    // HC-LOG-OTHER-UNCHANGED
    [Fact]
    public async Task GetEvents_WhenSuccessful_LogsSummaryAsInformation()
    {
        await RunWithFactoryAsync(
            _ => { },
            async factory =>
            {
                (await factory.CreateClient().GetAsync(EventListPath)).StatusCode.Should().Be(HttpStatusCode.OK);

                var summary = await WaitForSummaryAsync(factory, EventListPath);
                summary.Level.Should().Be(LogEventLevel.Information);
            });
    }

    // HC-LOG-OTHER-ERROR
    [Fact]
    public async Task GetEvents_WhenDatabaseUnavailableAndCacheMissed_LogsSummaryAsError()
    {
        await RunWithFactoryAsync(
            factory =>
            {
                factory.MinimumLogLevel = "Debug";
                factory.HostConnectionString = HealthCheckWebApplicationFactory.UnreachableDatabaseConnectionString;
            },
            async factory =>
            {
                var client = factory.CreateClient();

                // 快取命中時不會查 DB，500 就無從發生；先確立未命中。
                using (var scope = factory.Services.CreateScope())
                {
                    var queryCache = scope.ServiceProvider.GetRequiredService<IQueryCache>();
                    await queryCache.RemoveAsync(GetEventsHandler.CacheKey, CancellationToken.None);
                    var cached = await queryCache.GetAsync<object>(GetEventsHandler.CacheKey, CancellationToken.None);
                    cached.IsHit.Should().BeFalse();
                }

                (await client.GetAsync(EventListPath)).StatusCode.Should().Be(HttpStatusCode.InternalServerError);

                var summary = await WaitForSummaryAsync(factory, EventListPath);
                summary.Level.Should().Be(LogEventLevel.Error);
                GetEventsWithSameTraceId(factory, summary).Should().Contain(logEvent =>
                    GetSourceContext(logEvent) == DatabaseConnectionSourceContext);
            });
    }

    private static string BuildUnreachableConnectionString(string password)
        => $"Host=127.0.0.1;Port=1;Database=projectc_tests;Username=projectc_tests;Password={password}";

    private static async Task<LogEvent> WaitForSummaryAsync(HealthCheckWebApplicationFactory factory, string path)
    {
        var deadline = DateTime.UtcNow + SummaryPollDeadline;
        while (DateTime.UtcNow < deadline)
        {
            var summary = GetSummaries(factory, path).FirstOrDefault();
            if (summary is not null)
            {
                return summary;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException($"{SummaryPollDeadline} 內沒有等到 {path} 的請求摘要。");
    }

    private static List<LogEvent> GetSummaries(HealthCheckWebApplicationFactory factory, string path)
        => factory.LogSink.Events
            .Where(logEvent => GetSourceContext(logEvent) == RequestLoggingSourceContext
                && GetScalarString(logEvent, "RequestPath") == path)
            .ToList();

    private static List<LogEvent> GetEventsWithSameTraceId(HealthCheckWebApplicationFactory factory, LogEvent summary)
    {
        var traceId = GetRequiredTraceId(summary);
        return factory.LogSink.Events.Where(logEvent => GetScalarString(logEvent, "TraceId") == traceId).ToList();
    }

    private static string GetRequiredTraceId(LogEvent logEvent)
    {
        var traceId = GetScalarString(logEvent, "TraceId");
        traceId.Should().NotBeNullOrWhiteSpace("關聯事件前，摘要必須帶非空白的 TraceId，否則比對沒有意義");
        return traceId!;
    }

    private static string? GetSourceContext(LogEvent logEvent) => GetScalarString(logEvent, Constants.SourceContextPropertyName);

    private static string? GetScalarString(LogEvent logEvent, string propertyName)
        => logEvent.Properties.TryGetValue(propertyName, out var value) && value is ScalarValue { Value: string text }
            ? text
            : null;

    private static void AssertNoEventContains(IEnumerable<LogEvent> logEvents, string marker)
    {
        foreach (var logEvent in logEvents)
        {
            logEvent.RenderMessage().Should().NotContain(marker);
            logEvent.MessageTemplate.Text.Should().NotContain(marker);
            (logEvent.Exception?.ToString() ?? string.Empty).Should().NotContain(marker);
            foreach (var property in logEvent.Properties)
            {
                property.Value.ToString().Should().NotContain(marker, $"屬性 {property.Key} 不得含密碼");
            }
        }
    }
}
