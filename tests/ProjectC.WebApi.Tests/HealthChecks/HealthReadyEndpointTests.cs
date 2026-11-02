using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using ProjectC.WebApi.Tests.TestSupport;
using static ProjectC.WebApi.Tests.HealthChecks.HealthEndpointTestSupport;

namespace ProjectC.WebApi.Tests.HealthChecks;

// 就緒探測決定 Pod 是否接流量：DB 不可用時核心流程無法運作須移出，而所有 Pod 共用的 Redis、Seq
// 故障若納入判定，會把可降級的功能變成全站中斷。
public class HealthReadyEndpointTests : IClassFixture<HealthCheckWebApplicationFactory>
{
    private const string DatabaseCheckName = "database";

    private readonly HealthCheckWebApplicationFactory _factory;

    public HealthReadyEndpointTests(HealthCheckWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // HC-READY-HEALTHY、HC-BODY-HEALTHY
    [Fact]
    public async Task GetHealthReady_WhenDatabaseReachable_ReturnsOkWithHealthyBody()
    {
        var response = await _factory.CreateClient().GetAsync(ReadyPath);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("Healthy");
    }

    // HC-READY-DB-DOWN、HC-BODY-UNHEALTHY
    [Fact]
    public async Task GetHealthReady_WhenDatabaseRefusesConnection_ReturnsServiceUnavailableWithUnhealthyBody()
    {
        await RunWithFactoryAsync(
            factory => factory.HostConnectionString = HealthCheckWebApplicationFactory.UnreachableDatabaseConnectionString,
            async factory =>
            {
                var response = await factory.CreateClient().GetAsync(ReadyPath);

                response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
                (await response.Content.ReadAsStringAsync()).Should().Be("Unhealthy");
            });
    }

    // HC-READY-DB-MISSING
    [Fact]
    public async Task GetHealthReady_WhenTargetDatabaseMissing_ReturnsServiceUnavailable()
    {
        await RunWithFactoryAsync(
            factory => factory.HostDatabaseName = "projectc_database_that_does_not_exist",
            async factory =>
            {
                var response = await factory.CreateClient().GetAsync(ReadyPath);

                response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            });
    }

    // HC-READY-TIMEOUT：運作中的 Pod 遇到 DB 凍結是最常見的情況，此時若重用池中的閒置連線，連線 Timeout 不起作用。
    [Fact]
    public async Task GetHealthReady_WhenDatabaseFreezesAfterSuccessfulProbe_ReturnsServiceUnavailableWithinTimeout()
    {
        FreezableTcpProxy? proxy = null;
        try
        {
            await RunWithFactoryAsync(
                factory => proxy = factory.CreateFreezableDatabaseProxy(),
                async factory =>
                {
                    var client = factory.CreateClient();
                    (await client.GetAsync(ReadyPath)).StatusCode.Should().Be(HttpStatusCode.OK);

                    proxy!.Freeze();

                    var stopwatch = Stopwatch.StartNew();
                    var response = await client.GetAsync(ReadyPath);
                    await response.Content.ReadAsStringAsync();
                    stopwatch.Stop();

                    response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
                    stopwatch.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1.5));
                    stopwatch.Elapsed.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(4));
                });
        }
        finally
        {
            if (proxy is not null)
            {
                await proxy.DisposeAsync();
            }
        }
    }

    // HC-READY-TIMEOUT：探針週期與 failureThreshold 依賴這個上限，無回應的 DB 不得拖住探測。
    [Fact]
    public async Task GetHealthReady_WhenDatabaseDoesNotRespond_ReturnsServiceUnavailableWithinTimeout()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var acceptTask = listener.AcceptTcpClientAsync();

            await RunWithFactoryAsync(
                factory => factory.HostConnectionString =
                    $"Host=127.0.0.1;Port={port};Database=projectc_tests;Username=projectc_tests;Password=projectc_tests",
                async factory =>
                {
                    var client = factory.CreateClient();
                    // 先完成 host 啟動，量測值才只反映探測本身。
                    (await client.GetAsync(LivePath)).StatusCode.Should().Be(HttpStatusCode.OK);

                    var stopwatch = Stopwatch.StartNew();
                    var response = await client.GetAsync(ReadyPath);
                    await response.Content.ReadAsStringAsync();
                    stopwatch.Stop();

                    response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
                    stopwatch.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1.5));
                    stopwatch.Elapsed.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(4));
                    acceptTask.IsCompletedSuccessfully.Should().BeTrue("探測必須確實連到無回應的端點，否則時間量測沒有意義");
                });

            (await acceptTask).Dispose();
        }
        finally
        {
            listener.Stop();
        }
    }

    // HC-READY-TIMEOUT：時間量測在 2 到 4 秒之間分辨不出錯誤設定值（例如 3 秒）。
    [Fact]
    public void DatabaseReadinessRegistration_Timeout_IsTwoSeconds()
    {
        var registration = GetRegistrations(_factory.Services)
            .SingleOrDefault(candidate => candidate.Name == DatabaseCheckName);

        registration.Should().NotBeNull();
        registration!.Timeout.Should().Be(TimeSpan.FromSeconds(2));
    }

    // HC-READY-REDIS-DOWN
    [Fact]
    public async Task GetHealthReady_WhenRedisUnavailable_ReturnsOk()
    {
        var factory = new RedisUnavailableWebApplicationFactory();
        await factory.InitializeAsync();
        try
        {
            var response = await factory.CreateClient().GetAsync(ReadyPath);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await ((IAsyncLifetime)factory).DisposeAsync();
        }
    }

    // HC-READY-SEQ-DOWN
    [Fact]
    public async Task GetHealthReady_WhenSeqUnavailable_ReturnsOk()
    {
        await RunWithFactoryAsync(
            factory => factory.SeqServerUrl = UnreachableSeqServerUrl,
            async factory =>
            {
                var response = await factory.CreateClient().GetAsync(ReadyPath);

                factory.Services.GetRequiredService<IConfiguration>()["Seq:ServerUrl"].Should().Be(UnreachableSeqServerUrl);
                response.StatusCode.Should().Be(HttpStatusCode.OK);
            });
    }

    // HC-READY-ONLY-DATABASE：擋下日後新增的 Redis、Seq check。
    [Fact]
    public void HealthCheckRegistrations_Always_ContainOnlyDatabase()
    {
        GetRegistrations(_factory.Services).Select(registration => registration.Name)
            .Should().Equal(DatabaseCheckName);
    }

    // HC-READY-ONLY-DATABASE：只看狀態碼分不出「呼叫了但忽略結果」，所以斷言實際執行次數。
    [Fact]
    public async Task GetHealthReady_Always_RunsOnlyReadyTaggedChecks()
    {
        await RunWithFactoryAsync(
            factory => factory.IsHealthCheckCountingEnabled = true,
            async factory =>
            {
                var response = await factory.CreateClient().GetAsync(ReadyPath);

                response.StatusCode.Should().Be(HttpStatusCode.OK);
                factory.HealthCheckExecutionCounts.GetValueOrDefault(DatabaseCheckName).Should().Be(1);
                factory.HealthCheckExecutionCounts.GetValueOrDefault(HealthCheckWebApplicationFactory.UntaggedProbeCheckName).Should().Be(0);
            });
    }

    // HC-ANONYMOUS：探針不帶憑證。
    [Fact]
    public async Task GetHealthReady_WithoutAuthorizationHeader_ReturnsOk()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReadyPath);
        var client = _factory.CreateClient();

        var response = await client.SendAsync(request);

        request.Headers.Authorization.Should().BeNull();
        client.DefaultRequestHeaders.Authorization.Should().BeNull();
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // HC-NO-RATE-LIMIT
    [Fact]
    public Task GetHealthReady_WhenGlobalRateLimitExhausted_ReturnsOk()
        => AssertGlobalLimiterDoesNotApplyAsync(ReadyPath);

    // HC-NO-ENDPOINT-RATE-LIMIT
    [Fact]
    public Task GetHealthReady_WhenLoginRateLimitExhausted_ReturnsOk()
        => AssertLoginLimiterDoesNotApplyAsync(ReadyPath);

    // HC-NO-RATE-LIMIT-POLICY
    [Fact]
    public void HealthEndpoints_Metadata_DisableRateLimitingWithoutPolicy()
        => AssertRateLimitingExplicitlyDisabled(_factory.Services, ReadyPath);

    // HC-BODY-EXCEPTION
    [Fact]
    public async Task GetHealthReady_WhenHealthCheckServiceThrows_ReturnsGenericProblemDetails()
    {
        await RunWithFactoryAsync(
            factory => factory.HealthCheckServiceExceptionMessage = HealthCheckWebApplicationFactory.DefaultThrownExceptionMessage,
            async factory => await AssertUnexpectedExceptionResponseAsync(await factory.CreateClient().GetAsync(ReadyPath)));
    }

    private static IEnumerable<HealthCheckRegistration> GetRegistrations(IServiceProvider services)
        => services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
}
