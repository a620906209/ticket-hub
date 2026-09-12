using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using ProjectC.Infrastructure.Tickets;

namespace ProjectC.WebApi.Tests.Startup;

// query-caching spec：QC-TTL-CONFIG-001／002／003（design.md 決策 6a；比照既有
// PurchaseQueueOptionsFailFastTests 的手法，tasks.md 1.4a）。
public class QueryCacheOptionsFailFastTests
{
    private static Dictionary<string, string?> BaseConfiguration(string eventListTtlSeconds, string ticketTypesTtlSeconds) => new()
    {
        ["Jwt:Issuer"] = "ProjectC.Tests",
        ["Jwt:Audience"] = "ProjectC.Tests.Client",
        ["Jwt:SigningKey"] = "integration-test-signing-key-not-for-prod-32+",
        ["Jwt:AccessTokenExpirationMinutes"] = "30",
        [$"{TicketSigningOptions.SectionName}:{nameof(TicketSigningOptions.SigningKey)}"] = new string('x', 32),
        ["QueryCache:EventListTtlSeconds"] = eventListTtlSeconds,
        ["QueryCache:TicketTypesTtlSeconds"] = ticketTypesTtlSeconds,
    };

    private static bool ContainsOptionsValidationException(Exception exception)
    {
        var current = exception;
        while (current is not null)
        {
            if (current is OptionsValidationException)
            {
                return true;
            }

            current = current.InnerException;
        }

        return false;
    }

    private static WebApplicationFactory<Program> CreateFactory(string eventListTtlSeconds, string ticketTypesTtlSeconds)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configBuilder) =>
                configBuilder.AddInMemoryCollection(BaseConfiguration(eventListTtlSeconds, ticketTypesTtlSeconds)));
        });

    [Fact]
    public void CreatingHost_WithAllPositiveValues_DoesNotThrow()
    {
        using var factory = CreateFactory("30", "10");

        var act = () => factory.Server;

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("0", "10")]
    [InlineData("-1", "10")]
    [InlineData("30", "0")]
    [InlineData("30", "-1")]
    public void CreatingHost_WithAnyZeroOrNegativeValue_ThrowsOptionsValidationException(string eventListTtlSeconds, string ticketTypesTtlSeconds)
    {
        using var factory = CreateFactory(eventListTtlSeconds, ticketTypesTtlSeconds);

        var act = () => factory.Server;

        act.Should().Throw<Exception>()
            .Where(e => e is OptionsValidationException || ContainsOptionsValidationException(e));
    }

    [Theory]
    [InlineData("abc", "10")]
    [InlineData("30", "abc")]
    public void CreatingHost_WithNonParsableValue_ThrowsException(string eventListTtlSeconds, string ticketTypesTtlSeconds)
    {
        // 無法解析為整數時，.Bind(...) 本身就會在啟動時拋出例外（不是 OptionsValidationException，
        // 而是 Bind 失敗——design.md 決策 6a：兩者都達到 fail-fast 的效果，這裡只斷言啟動確實失敗）。
        using var factory = CreateFactory(eventListTtlSeconds, ticketTypesTtlSeconds);

        var act = () => factory.Server;

        act.Should().Throw<Exception>();
    }

    // 完全不提供 QueryCache 的 key（不是設成 0／空字串，是設定檔中根本沒有這一段）：MUST 清空既有的
    // appsettings.json 設定來源，否則會被該檔案已寫入的合法出廠預設值（30／10）蓋過，測不出「缺漏時
    // 維持 C# 預設值 0、被 [Range] 擋下」這個效果。清空後其他同樣 ValidateOnStart 的設定類別
    // （PurchaseQueueOptions）須一併補上合法值，避免它先於 QueryCacheOptions 失敗、讓斷言失去針對性。
    private static Dictionary<string, string?> FullConfigurationWithoutQueryCache() => new()
    {
        ["Jwt:Issuer"] = "ProjectC.Tests",
        ["Jwt:Audience"] = "ProjectC.Tests.Client",
        ["Jwt:SigningKey"] = "integration-test-signing-key-not-for-prod-32+",
        ["Jwt:AccessTokenExpirationMinutes"] = "30",
        [$"{TicketSigningOptions.SectionName}:{nameof(TicketSigningOptions.SigningKey)}"] = new string('x', 32),
        ["PurchaseQueue:MaxConcurrentAdmittedBuyers"] = "50",
        ["PurchaseQueue:AdmissionTtlSeconds"] = "300",
        ["PurchaseQueue:PollingIntervalSeconds"] = "5",
    };

    [Theory]
    [InlineData("EventListTtlSeconds")]
    [InlineData("TicketTypesTtlSeconds")]
    public void CreatingHost_WithKeyEntirelyMissingFromConfiguration_ThrowsOptionsValidationException(string keyToOmit)
    {
        var configuration = FullConfigurationWithoutQueryCache();
        if (keyToOmit != "EventListTtlSeconds")
        {
            configuration["QueryCache:EventListTtlSeconds"] = "30";
        }

        if (keyToOmit != "TicketTypesTtlSeconds")
        {
            configuration["QueryCache:TicketTypesTtlSeconds"] = "10";
        }

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.Sources.Clear();
                configBuilder.AddInMemoryCollection(configuration);
            });
        });

        var act = () => factory.Server;

        act.Should().Throw<Exception>()
            .Where(e => e is OptionsValidationException || ContainsOptionsValidationException(e));
    }
}
