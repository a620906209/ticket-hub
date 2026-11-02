using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using ProjectC.WebApi.Tests.TestSupport;
using static ProjectC.WebApi.Tests.HealthChecks.HealthEndpointTestSupport;

namespace ProjectC.WebApi.Tests.HealthChecks;

// 存活探測若依賴外部服務，相依故障會讓平台重啟健康的行程，而重啟修不好外部故障。
public class HealthLiveEndpointTests : IClassFixture<HealthCheckWebApplicationFactory>
{
    private readonly HealthCheckWebApplicationFactory _factory;

    public HealthLiveEndpointTests(HealthCheckWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // HC-LIVE-HEALTHY、HC-BODY-HEALTHY
    [Fact]
    public async Task GetHealthLive_WhenDependenciesHealthy_ReturnsOkWithHealthyBody()
    {
        var response = await _factory.CreateClient().GetAsync(LivePath);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("Healthy");
    }

    // HC-LIVE-NO-CHECKS
    [Fact]
    public async Task GetHealthLive_Always_RunsNoHealthChecks()
    {
        await RunWithFactoryAsync(
            factory => factory.IsHealthCheckCountingEnabled = true,
            async factory =>
            {
                var response = await factory.CreateClient().GetAsync(LivePath);

                response.StatusCode.Should().Be(HttpStatusCode.OK);
                factory.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
                    .Select(registration => registration.Name)
                    .Should().Contain(HealthCheckWebApplicationFactory.UntaggedProbeCheckName, "探查 check 必須已註冊，次數為 0 才有意義");
                factory.HealthCheckExecutionCounts.Should().BeEmpty();
            });
    }

    // HC-LIVE-DB-DOWN
    [Fact]
    public async Task GetHealthLive_WhenDatabaseUnavailable_ReturnsOk()
    {
        await RunWithFactoryAsync(
            factory => factory.HostConnectionString = HealthCheckWebApplicationFactory.UnreachableDatabaseConnectionString,
            async factory =>
            {
                var response = await factory.CreateClient().GetAsync(LivePath);

                response.StatusCode.Should().Be(HttpStatusCode.OK);
            });
    }

    // HC-LIVE-REDIS-DOWN
    [Fact]
    public async Task GetHealthLive_WhenRedisUnavailable_ReturnsOk()
    {
        var factory = new RedisUnavailableWebApplicationFactory();
        await factory.InitializeAsync();
        try
        {
            var response = await factory.CreateClient().GetAsync(LivePath);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await ((IAsyncLifetime)factory).DisposeAsync();
        }
    }

    // HC-LIVE-SEQ-DOWN
    [Fact]
    public async Task GetHealthLive_WhenSeqUnavailable_ReturnsOk()
    {
        await RunWithFactoryAsync(
            factory => factory.SeqServerUrl = UnreachableSeqServerUrl,
            async factory =>
            {
                var response = await factory.CreateClient().GetAsync(LivePath);

                factory.Services.GetRequiredService<IConfiguration>()["Seq:ServerUrl"].Should().Be(UnreachableSeqServerUrl);
                response.StatusCode.Should().Be(HttpStatusCode.OK);
            });
    }

    // HC-ANONYMOUS：探針不帶憑證。
    [Fact]
    public async Task GetHealthLive_WithoutAuthorizationHeader_ReturnsOk()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LivePath);
        var client = _factory.CreateClient();

        var response = await client.SendAsync(request);

        request.Headers.Authorization.Should().BeNull();
        client.DefaultRequestHeaders.Authorization.Should().BeNull();
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // HC-NO-RATE-LIMIT
    [Fact]
    public Task GetHealthLive_WhenGlobalRateLimitExhausted_ReturnsOk()
        => AssertGlobalLimiterDoesNotApplyAsync(LivePath);

    // HC-NO-ENDPOINT-RATE-LIMIT
    [Fact]
    public Task GetHealthLive_WhenLoginRateLimitExhausted_ReturnsOk()
        => AssertLoginLimiterDoesNotApplyAsync(LivePath);

    // HC-NO-RATE-LIMIT-POLICY
    [Fact]
    public void HealthEndpoints_Metadata_DisableRateLimitingWithoutPolicy()
        => AssertRateLimitingExplicitlyDisabled(_factory.Services, LivePath);

    // HC-BODY-EXCEPTION
    [Fact]
    public async Task GetHealthLive_WhenHealthCheckServiceThrows_ReturnsGenericProblemDetails()
    {
        await RunWithFactoryAsync(
            factory => factory.HealthCheckServiceExceptionMessage = HealthCheckWebApplicationFactory.DefaultThrownExceptionMessage,
            async factory => await AssertUnexpectedExceptionResponseAsync(await factory.CreateClient().GetAsync(LivePath)));
    }
}
