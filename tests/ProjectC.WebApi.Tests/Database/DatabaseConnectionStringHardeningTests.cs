using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Infrastructure.Persistence;
using ProjectC.WebApi.Extensions;

namespace ProjectC.WebApi.Tests.Database;

// db-timeout-pool-tuning hardener：連線字串缺漏或格式錯誤時，不新增自訂例外、也不得把密碼帶進例外訊息。
public class DatabaseConnectionStringHardeningTests
{
    private const string PasswordSentinel = "SECRET-SENTINEL";

    private static ServiceProvider BuildProvider(string? connectionString)
    {
        var settings = new Dictionary<string, string?>();
        if (connectionString is not null)
        {
            settings["ConnectionStrings:DefaultConnection"] = connectionString;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddPersistence(configuration);
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ResolvingDbContext_WithMissingConnectionString_DoesNotThrowFromOptionsAssembly(string? connectionString)
    {
        using var provider = BuildProvider(connectionString);
        using var scope = provider.CreateScope();

        var act = () => scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        act.Should().NotThrow();
    }

    [Fact]
    public void ResolvingDbContext_WithMalformedConnectionString_DoesNotLeakPassword()
    {
        using var provider = BuildProvider($"Host=unused;Password={PasswordSentinel};UnknownKeyword=1");
        using var scope = provider.CreateScope();

        var act = () => scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.GetDbConnection();

        var exception = act.Should().Throw<Exception>().Which;
        exception.ToString().Should().NotContain(PasswordSentinel);
    }
}
