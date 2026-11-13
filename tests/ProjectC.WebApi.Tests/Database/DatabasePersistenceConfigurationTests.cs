using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using ProjectC.Infrastructure.Persistence;
using ProjectC.WebApi.HealthChecks;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Database;

// db-timeout-pool-tuning DBL-001～004：Database 設定確實套用到正式註冊的 ApplicationDbContext。
public class DatabasePersistenceConfigurationTests : IClassFixture<DatabaseSettingsWebApplicationFactory>
{
    private readonly DatabaseSettingsWebApplicationFactory _factory;

    public DatabasePersistenceConfigurationTests(DatabaseSettingsWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private WebApplicationFactory<Program> CreateHost(Dictionary<string, string?> settings)
        => _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configBuilder) => configBuilder.AddInMemoryCollection(settings)));

    [Fact]
    public void ApplicationDbContext_WithoutDatabaseSection_UsesDefaultTimeoutsAndPoolSize()
    {
        using var host = CreateHost([]);
        using var scope = host.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var builder = new NpgsqlConnectionStringBuilder(dbContext.Database.GetConnectionString());
        dbContext.Database.GetCommandTimeout().Should().Be(30);
        builder.Timeout.Should().Be(15);
        builder.MaxPoolSize.Should().Be(100);
    }

    [Fact]
    public void ApplicationDbContext_WithDatabaseSection_AppliesCommandTimeoutAndConnectionStringValues()
    {
        using var host = CreateHost(new()
        {
            ["Database:CommandTimeoutSeconds"] = "7",
            ["Database:ConnectionTimeoutSeconds"] = "5",
            ["Database:MaxPoolSize"] = "20",
        });
        using var scope = host.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var builder = new NpgsqlConnectionStringBuilder(dbContext.Database.GetConnectionString());
        dbContext.Database.GetCommandTimeout().Should().Be(7);
        builder.Timeout.Should().Be(5);
        builder.MaxPoolSize.Should().Be(20);
    }

    [Fact]
    public void ApplicationDbContext_WhenConnectionStringHasSameParameters_DatabaseSettingsWin()
    {
        var testConnection = new NpgsqlConnectionStringBuilder(_factory.DatabaseConnectionString)
        {
            MaxPoolSize = 500,
            Timeout = 60,
        };
        using var host = CreateHost(new()
        {
            ["ConnectionStrings:DefaultConnection"] = testConnection.ConnectionString,
            ["Database:MaxPoolSize"] = "20",
            ["Database:ConnectionTimeoutSeconds"] = "5",
        });
        using var scope = host.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var builder = new NpgsqlConnectionStringBuilder(dbContext.Database.GetConnectionString());
        builder.MaxPoolSize.Should().Be(20);
        builder.Timeout.Should().Be(5);
    }

    [Fact]
    public async Task DatabaseReadinessHealthCheck_WithLargeDatabaseSettings_StillUsesTwoSecondTimeoutWithoutPooling()
    {
        using var host = CreateHost(new()
        {
            ["Database:ConnectionTimeoutSeconds"] = "30",
            ["Database:MaxPoolSize"] = "20",
        });
        using var scope = host.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var result = await new DatabaseReadinessHealthCheck(dbContext).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        var builder = new NpgsqlConnectionStringBuilder(dbContext.Database.GetConnectionString());
        builder.Timeout.Should().Be(2);
        builder.Pooling.Should().BeFalse();
    }
}
