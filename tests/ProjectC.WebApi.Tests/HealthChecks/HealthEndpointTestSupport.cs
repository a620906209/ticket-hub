using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.HealthChecks;

internal static class HealthEndpointTestSupport
{
    public const string LivePath = "/health/live";
    public const string ReadyPath = "/health/ready";
    public const string UnreachableSeqServerUrl = "http://127.0.0.1:1";

    private const int ProbeRequestCount = 50;

    public static async Task RunWithFactoryAsync(
        Action<HealthCheckWebApplicationFactory> configureFactory,
        Func<HealthCheckWebApplicationFactory, Task> runTestAsync)
    {
        var factory = new HealthCheckWebApplicationFactory();
        await factory.InitializeAsync();
        try
        {
            configureFactory(factory);
            await runTestAsync(factory);
        }
        finally
        {
            await ((IAsyncLifetime)factory).DisposeAsync();
        }
    }

    public static Task<HttpResponseMessage> SendEventListControlRequestAsync(HttpClient client, CancellationToken cancellationToken)
        => client.GetAsync("/api/events", cancellationToken);

    // 限流早於模型繫結，本文內容不影響計數（api-health-checks design.md 決策 8）。
    public static Task<HttpResponseMessage> SendLoginControlRequestAsync(HttpClient client, CancellationToken cancellationToken)
        => client.PostAsJsonAsync("/api/auth/login", new { }, cancellationToken);

    public static async Task AssertGlobalLimiterDoesNotApplyAsync(string path)
    {
        await RunWithFactoryAsync(
            factory => factory.IsGlobalLimiterEnabled = true,
            async factory =>
            {
                await factory.ExhaustRateLimitAsync(SendEventListControlRequestAsync);
                await AssertAllProbesReturnOkAsync(factory.CreateClient(), path);
            });
    }

    public static async Task AssertLoginLimiterDoesNotApplyAsync(string path)
    {
        await RunWithFactoryAsync(
            factory => factory.LoginPermitLimit = 1,
            async factory =>
            {
                await factory.ExhaustRateLimitAsync(SendLoginControlRequestAsync);
                await AssertAllProbesReturnOkAsync(factory.CreateClient(), path);
            });
    }

    public static void AssertRateLimitingExplicitlyDisabled(IServiceProvider services, string path)
    {
        var endpoint = services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SingleOrDefault(candidate => candidate.RoutePattern.RawText == path);

        endpoint.Should().NotBeNull($"{path} 必須已對應為端點");
        endpoint!.Metadata.GetMetadata<DisableRateLimitingAttribute>().Should().NotBeNull();
        endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>().Should().BeNull();
    }

    public static async Task AssertUnexpectedExceptionResponseAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        root.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo("status", "title", "traceId");
        root.GetProperty("status").GetInt32().Should().Be(StatusCodes.Status500InternalServerError);
        root.GetProperty("title").GetString().Should().Be("An unexpected error occurred.");

        var traceId = root.GetProperty("traceId").GetString();
        traceId.Should().MatchRegex(@"^[\w:.|-]+$");

        // 隨機識別碼可能剛好含 seq 等字樣，掃描前先移除。
        var bodyWithoutTraceId = body.Replace(traceId!, string.Empty, StringComparison.Ordinal);
        foreach (var forbiddenText in new[]
        {
            HealthCheckWebApplicationFactory.ThrownExceptionMarker,
            "marker-", "Host=", "Password", nameof(InvalidOperationException), " at ",
            "database", "redis", "seq", "postgres", "npgsql",
        })
        {
            bodyWithoutTraceId.Should().NotContainEquivalentOf(forbiddenText);
        }
    }

    private static async Task AssertAllProbesReturnOkAsync(HttpClient client, string path)
    {
        var statusCodes = new List<HttpStatusCode>();
        for (var requestIndex = 0; requestIndex < ProbeRequestCount; requestIndex++)
        {
            using var response = await client.GetAsync(path);
            statusCodes.Add(response.StatusCode);
        }

        statusCodes.Should().HaveCount(ProbeRequestCount);
        statusCodes.Should().OnlyContain(statusCode => statusCode == HttpStatusCode.OK);
    }
}
