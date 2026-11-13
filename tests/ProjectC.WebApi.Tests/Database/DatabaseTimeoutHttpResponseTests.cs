using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Infrastructure.Persistence;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Database;

// db-timeout-pool-tuning AER-DB-001／002 的 HTTP 層：資料庫逾時經 GlobalExceptionHandler 回標準 503 ProblemDetails。
// GET /api/events/{id} 走 GetEventByIdHandler，未經 IQueryCache（只有 GetEventsHandler 使用），每次都會查 DB；
// 查詢目標為 Events 資料表，所以不需要預先建立活動——不存在的 id 一樣會先被鎖卡住。
public class DatabaseTimeoutHttpResponseTests : IClassFixture<DatabaseSettingsWebApplicationFactory>
{
    private readonly DatabaseSettingsWebApplicationFactory _factory;

    public DatabaseTimeoutHttpResponseTests(DatabaseSettingsWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private WebApplicationFactory<Program> CreateHost(Dictionary<string, string?> settings)
        => _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configBuilder) => configBuilder.AddInMemoryCollection(settings)));

    [Fact]
    public async Task GetEventById_WhenDatabaseCommandTimesOut_Returns503ProblemDetails()
    {
        using var host = CreateHost(new() { ["Database:CommandTimeoutSeconds"] = "1" });
        using var client = host.CreateClient();

        // 以獨立連線（不經 app 的連線池）持有 Events 的排他鎖，使目標查詢卡住直到命令逾時。
        await using var lockingContext = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(_factory.DatabaseConnectionString).Options);
        await using var transaction = await lockingContext.Database.BeginTransactionAsync();
        // 固定字串、無外部輸入。
#pragma warning disable EF1002
        await lockingContext.Database.ExecuteSqlRawAsync("LOCK TABLE \"Events\" IN ACCESS EXCLUSIVE MODE");
#pragma warning restore EF1002

        try
        {
            var response = await client.GetAsync($"/api/events/{Guid.NewGuid()}");

            await AssertServiceUnavailableAsync(response);
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    [Fact]
    public async Task GetEventById_WhenConnectionPoolExhausted_Returns503ProblemDetails()
    {
        using var host = CreateHost(new()
        {
            ["Database:MaxPoolSize"] = "1",
            ["Database:ConnectionTimeoutSeconds"] = "1",
        });
        using var client = host.CreateClient();

        using var holderScope = host.Services.CreateScope();
        var holder = holderScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await holder.Database.OpenConnectionAsync();

        try
        {
            var response = await client.GetAsync($"/api/events/{Guid.NewGuid()}");

            await AssertServiceUnavailableAsync(response);
        }
        finally
        {
            await holder.Database.CloseConnectionAsync();
        }
    }

    private static async Task AssertServiceUnavailableAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        using var problemDetails = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problemDetails.RootElement.GetProperty("status").GetInt32().Should().Be(503);
        problemDetails.RootElement.GetProperty("title").GetString().Should().Be("ServiceUnavailable");
        problemDetails.RootElement.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
        problemDetails.RootElement.TryGetProperty("detail", out _).Should().BeFalse();
    }
}
