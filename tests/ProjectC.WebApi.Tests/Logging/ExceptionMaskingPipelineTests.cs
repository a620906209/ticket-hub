using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using ProjectC.Application.Common;
using ProjectC.Infrastructure.Security;
using ProjectC.WebApi.BackgroundServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProjectC.Application.Common.Interfaces;
using ProjectC.WebApi.Tests.TestSupport;
using Serilog.Core;

namespace ProjectC.WebApi.Tests.Logging;

// exception-log-masking tasks.md 4.1：真實 Serilog 管線（SerilogConfigurator）下，HTTP 路徑的例外日誌不含明文。
public sealed class ExceptionMaskingPipelineTests : IClassFixture<ThrowingQueryCacheWebApplicationFactory>
{
    private const string Secret = "s3cret-pw";

    private readonly ThrowingQueryCacheWebApplicationFactory _factory;

    public ExceptionMaskingPipelineTests(ThrowingQueryCacheWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // 對應 OBS-EXMASK-001、OBS-EXMASK-013
    [Fact]
    public async Task GetEvents_WhenDependencyThrowsWithPassword_LogsMaskedExceptionAndKeepsResponseShape()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _factory.LogSink.Clear();

        var response = await client.GetAsync("/api/events");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain(Secret);
        using var document = JsonDocument.Parse(body);
        document.RootElement.EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo(["status", "title", "traceId"]);

        var withException = _factory.LogSink.Events.Where(e => e.Exception is not null).ToList();
        withException.Should().NotBeEmpty();
        withException.Select(e => e.Exception!.ToString()).Should().AllSatisfy(text => text.Should().NotContain(Secret));

        var handlerEvent = withException.Single(e => e.Properties.TryGetValue(Constants.SourceContextPropertyName, out var context)
            && context.ToString().Contains("GlobalExceptionHandler"));
        handlerEvent.Exception!.ToString().Should().Contain("Password=***").And.Contain("InvalidOperationException");
        handlerEvent.Properties.Should().ContainKey("TraceId");
    }

    // 對應 OBS-EXMASK-002：背景服務路徑（非 HTTP）也經過同一條遮蔽管線。
    [Fact]
    public async Task ExpiredOrderCleanupService_WhenCycleFailsWithPassword_LogsMaskedException()
    {
        _factory.LogSink.Clear();
        var logger = _factory.Services.GetRequiredService<ILogger<ExpiredOrderCleanupService>>();
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope())
            .Throws(new InvalidOperationException("Host=db;Password=" + Secret + ";Database=x"));
        var service = new ExpiredOrderCleanupService(
            scopeFactory.Object, new SystemDateTimeProvider(), new OrderCleanupOptions(), logger);

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _factory.LogSink.Events.Any(e => e.RenderMessage().Contains("cleanup cycle failed", StringComparison.Ordinal)));
        await service.StopAsync(CancellationToken.None);

        var cycleFailure = _factory.LogSink.Events
            .Single(e => e.RenderMessage().Contains("cleanup cycle failed", StringComparison.Ordinal));
        cycleFailure.Exception.Should().NotBeNull();
        cycleFailure.Exception!.ToString().Should().Contain("Password=***").And.NotContain(Secret);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("背景服務未在 10 秒內記錄週期失敗日誌");
            }

            await Task.Delay(25);
        }
    }
}

public sealed class ThrowingQueryCacheWebApplicationFactory : CustomWebApplicationFactory
{
    public InMemoryLogEventSink LogSink { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services => services.AddSingleton<ILogEventSink>(LogSink));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IQueryCache>();
            services.AddSingleton<IQueryCache, ThrowingQueryCache>();
        });
    }

    private sealed class ThrowingQueryCache : IQueryCache
    {
        public Task<QueryCacheResult<T>> GetAsync<T>(string key, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Host=db;Password=s3cret-pw;Database=x");

        public Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemoveAsync(string key, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
