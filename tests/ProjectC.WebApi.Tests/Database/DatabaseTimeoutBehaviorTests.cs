using System.Diagnostics;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Infrastructure.Persistence;
using ProjectC.WebApi.Tests.TestSupport;
using Xunit.Abstractions;

namespace ProjectC.WebApi.Tests.Database;

// db-timeout-pool-tuning DBL-008／009：逾時設定在真實 PostgreSQL 上確實生效。只斷言上界，不斷言精確時間。
// Testing 環境不註冊 ExpiredOrderCleanupService／PurchaseQueueAdmissionService（BackgroundServiceCollectionExtensions），
// 所以 MaxPoolSize = 1 的測試中沒有背景服務會搶唯一的連線。
public class DatabaseTimeoutBehaviorTests : IClassFixture<DatabaseSettingsWebApplicationFactory>
{
    private static readonly TimeSpan UpperBound = TimeSpan.FromSeconds(5);

    private readonly DatabaseSettingsWebApplicationFactory _factory;
    private readonly ITestOutputHelper _output;

    public DatabaseTimeoutBehaviorTests(DatabaseSettingsWebApplicationFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    private WebApplicationFactory<Program> CreateHost(Dictionary<string, string?> settings)
        => _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configBuilder) => configBuilder.AddInMemoryCollection(settings)));

    private void DescribeExceptionChain(string label, Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            _output.WriteLine($"{label}: {current.GetType().FullName} | TargetSite={current.TargetSite?.DeclaringType?.Assembly.GetName().Name}");
        }
    }

    [Fact]
    public async Task ExecuteSql_WhenCommandExceedsCommandTimeout_FailsWithinBoundAndPoolRemainsUsable()
    {
        using var host = CreateHost(new() { ["Database:CommandTimeoutSeconds"] = "2" });
        using var scope = host.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var stopwatch = Stopwatch.StartNew();
        // 固定字串、無外部輸入。
#pragma warning disable EF1002
        var act = () => dbContext.Database.ExecuteSqlRawAsync("SELECT pg_sleep(6)");
#pragma warning restore EF1002
        var exception = (await act.Should().ThrowAsync<Exception>()).Which;
        stopwatch.Stop();

        DescribeExceptionChain("command-timeout", exception);
        stopwatch.Elapsed.Should().BeLessThan(UpperBound);
        ChainContains<TimeoutException>(exception).Should().BeTrue();

        using var followUpScope = host.Services.CreateScope();
        var followUp = followUpScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
#pragma warning disable EF1002
        var affected = await followUp.Database.ExecuteSqlRawAsync("SELECT 1");
#pragma warning restore EF1002
        affected.Should().Be(-1);
    }

    [Fact]
    public async Task OpenConnection_WhenPoolExhausted_FailsWithinConnectionTimeoutAndSucceedsAfterRelease()
    {
        using var host = CreateHost(new()
        {
            ["Database:MaxPoolSize"] = "1",
            ["Database:ConnectionTimeoutSeconds"] = "2",
        });
        using var holderScope = host.Services.CreateScope();
        var holder = holderScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await holder.Database.OpenConnectionAsync();

        using var waiterScope = host.Services.CreateScope();
        var waiter = waiterScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stopwatch = Stopwatch.StartNew();
        var act = () => waiter.Database.OpenConnectionAsync();
        var exception = (await act.Should().ThrowAsync<Exception>()).Which;
        stopwatch.Stop();

        DescribeExceptionChain("pool-exhausted", exception);
        stopwatch.Elapsed.Should().BeLessThan(UpperBound);
        ChainContains<TimeoutException>(exception).Should().BeTrue("池耗盡等待逾時的例外鏈必須含 TimeoutException，偵測器才會判為 503");

        await holder.Database.CloseConnectionAsync();

        using var afterReleaseScope = host.Services.CreateScope();
        var afterRelease = afterReleaseScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var canConnect = await afterRelease.Database.CanConnectAsync();
        canConnect.Should().BeTrue();
    }

    private static bool ChainContains<TException>(Exception exception) where TException : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException)
            {
                return true;
            }
        }

        return false;
    }
}
