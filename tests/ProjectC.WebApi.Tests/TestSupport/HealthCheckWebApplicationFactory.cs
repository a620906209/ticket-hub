using System.Collections.Concurrent;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Serilog.Core;

namespace ProjectC.WebApi.Tests.TestSupport;

// 所有覆寫值都必須在 InitializeAsync 之後、第一次存取 Server／Services／CreateClient() 之前設定才會生效。
// 連線字串覆寫只影響 host：基底 InitializeAsync 的 MigrateAsync 仍打在測試容器。
public sealed class HealthCheckWebApplicationFactory : CustomWebApplicationFactory
{
    public const string UnreachableDatabaseConnectionString =
        "Host=127.0.0.1;Port=1;Database=projectc_tests;Username=projectc_tests;Password=projectc_tests";

    public const string ThrownExceptionMarker = "marker-health-exception";

    // 刻意不含任何形似密碼的字串：GlobalExceptionHandler 會完整記錄例外，放入假密碼等於測試自己製造
    // 專案禁止的日誌（api-health-checks design.md 決策 9）。
    public const string DefaultThrownExceptionMessage = $"{ThrownExceptionMarker} Host=marker-db marker-redis";

    private const int MaximumControlRequestCount = 5;
    private static readonly TimeSpan ControlRequestDeadline = TimeSpan.FromSeconds(30);

    public InMemoryLogEventSink LogSink { get; } = new();

    public string? HostConnectionString { get; set; }

    public string? HostDatabaseName { get; set; }

    public string? HttpsPort { get; set; }

    public string? SeqServerUrl { get; set; }

    public string? MinimumLogLevel { get; set; }

    public int? LoginPermitLimit { get; set; }

    public bool IsGlobalLimiterEnabled { get; set; }

    public string? HealthCheckServiceExceptionMessage { get; set; }

    public bool IsHealthCheckCountingEnabled { get; set; }

    public const string UntaggedProbeCheckName = "untagged-probe";

    public ConcurrentDictionary<string, int> HealthCheckExecutionCounts { get; } = new();

    public FreezableTcpProxy CreateFreezableDatabaseProxy()
    {
        var builder = new NpgsqlConnectionStringBuilder(TestDatabaseConnectionString);
        var proxy = new FreezableTcpProxy(builder.Host!, builder.Port);
        builder.Host = "127.0.0.1";
        builder.Port = proxy.Port;
        HostConnectionString = builder.ConnectionString;
        return proxy;
    }

    // 額度為 1，最遲第 2 次就該回 429；未出現就明確失敗，避免限流沒生效時探測測試假通過。
    public async Task ExhaustRateLimitAsync(Func<HttpClient, CancellationToken, Task<HttpResponseMessage>> sendControlRequestAsync)
    {
        using var deadline = new CancellationTokenSource(ControlRequestDeadline);
        var client = CreateClient();

        for (var requestIndex = 0; requestIndex < MaximumControlRequestCount; requestIndex++)
        {
            using var response = await sendControlRequestAsync(client, deadline.Token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"控制端點在 {MaximumControlRequestCount} 次請求內沒有回 429，限流未生效，不得進入探測請求。");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        var hostConnectionString = ResolveHostConnectionString();
        if (hostConnectionString is not null)
        {
            TestHostConfiguration.ApplyCommonTestConfiguration(builder, hostConnectionString);
        }

        // 須在基底設定之後加入，否則 LoginRateLimiting:PermitLimit 會被基底的 1000 蓋掉。
        builder.ConfigureAppConfiguration((_, configBuilder) =>
            configBuilder.AddInMemoryCollection(BuildConfigurationOverrides()));

        builder.ConfigureServices(services => services.AddSingleton<ILogEventSink>(LogSink));

        builder.ConfigureTestServices(services =>
        {
            if (IsGlobalLimiterEnabled)
            {
                services.Configure<RateLimiterOptions>(options => options.GlobalLimiter = CreateExhaustibleGlobalLimiter());
            }

            if (HealthCheckServiceExceptionMessage is not null)
            {
                services.RemoveAll<HealthCheckService>();
                services.AddSingleton<HealthCheckService>(new ThrowingHealthCheckService(HealthCheckServiceExceptionMessage));
            }

            if (IsHealthCheckCountingEnabled)
            {
                services.PostConfigure<HealthCheckServiceOptions>(WrapRegistrationsWithCounters);
            }
        });
    }

    private string? ResolveHostConnectionString()
    {
        if (HostDatabaseName is null)
        {
            return HostConnectionString;
        }

        return new NpgsqlConnectionStringBuilder(TestDatabaseConnectionString) { Database = HostDatabaseName }.ConnectionString;
    }

    private Dictionary<string, string?> BuildConfigurationOverrides()
    {
        var overrides = new Dictionary<string, string?>();

        if (HttpsPort is not null)
        {
            overrides["HTTPS_PORT"] = HttpsPort;
        }

        if (SeqServerUrl is not null)
        {
            overrides["Seq:ServerUrl"] = SeqServerUrl;
        }

        if (MinimumLogLevel is not null)
        {
            overrides["Serilog:MinimumLevel:Default"] = MinimumLogLevel;
        }

        if (LoginPermitLimit is not null)
        {
            overrides["LoginRateLimiting:PermitLimit"] = LoginPermitLimit.Value.ToString();
            // 視窗過短時額度可能在探測途中恢復，讓控制端點的 429 失去證明力。
            overrides["LoginRateLimiting:WindowSeconds"] = "3600";
        }

        return overrides;
    }

    private static PartitionedRateLimiter<HttpContext> CreateExhaustibleGlobalLimiter()
        => PartitionedRateLimiter.Create<HttpContext, string>(_ => RateLimitPartition.GetFixedWindowLimiter(
            "health-check-tests",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 1,
                Window = TimeSpan.FromHours(1),
                QueueLimit = 0,
            }));

    private void WrapRegistrationsWithCounters(HealthCheckServiceOptions options)
    {
        foreach (var registration in options.Registrations)
        {
            var originalFactory = registration.Factory;
            var checkName = registration.Name;
            registration.Factory = serviceProvider =>
                new CountingHealthCheck(checkName, originalFactory(serviceProvider), HealthCheckExecutionCounts);
        }

        options.Registrations.Add(new HealthCheckRegistration(
            UntaggedProbeCheckName,
            _ => new CountingHealthCheck(UntaggedProbeCheckName, new AlwaysHealthyCheck(), HealthCheckExecutionCounts),
            failureStatus: null,
            tags: null));
    }

    private sealed class CountingHealthCheck : IHealthCheck
    {
        private readonly string _checkName;
        private readonly IHealthCheck _innerCheck;
        private readonly ConcurrentDictionary<string, int> _executionCounts;

        public CountingHealthCheck(string checkName, IHealthCheck innerCheck, ConcurrentDictionary<string, int> executionCounts)
        {
            _checkName = checkName;
            _innerCheck = innerCheck;
            _executionCounts = executionCounts;
        }

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            _executionCounts.AddOrUpdate(_checkName, 1, (_, currentCount) => currentCount + 1);
            return _innerCheck.CheckHealthAsync(context, cancellationToken);
        }
    }

    private sealed class AlwaysHealthyCheck : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(HealthCheckResult.Healthy());
    }

    private sealed class ThrowingHealthCheckService : HealthCheckService
    {
        private readonly string _exceptionMessage;

        public ThrowingHealthCheckService(string exceptionMessage)
        {
            _exceptionMessage = exceptionMessage;
        }

        public override Task<HealthReport> CheckHealthAsync(
            Func<HealthCheckRegistration, bool>? predicate,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(_exceptionMessage);
    }
}
