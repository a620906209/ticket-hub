using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Infrastructure.Tickets;

namespace ProjectC.WebApi.Tests.Startup;

// 特徵化測試：鎖定 Program.cs 的 DI 圖（服務型別、實作型別、lifetime），讓拆分 composition root
// 這類純重構能證明「註冊內容沒有改變」（webapi-composition-root-cleanup design.md 決策 3）。
// 以排序後的多重集合比對，不比對註冊順序。基準檔有意變更時，設定環境變數
// UPDATE_SERVICE_REGISTRATION_SNAPSHOT=1 重新產生，並在審查時檢視差異。
public class ServiceRegistrationSnapshotTests
{
    private const string UpdateEnvironmentVariable = "UPDATE_SERVICE_REGISTRATION_SNAPSHOT";
    private const string BaselineFileName = "ServiceRegistrationSnapshot.baseline.txt";

    [Fact]
    public void ServiceRegistrations_MatchCommittedBaseline()
    {
        var actualLines = CaptureProjectServiceRegistrations();

        if (Environment.GetEnvironmentVariable(UpdateEnvironmentVariable) == "1")
        {
            File.WriteAllLines(GetSourceBaselinePath(), actualLines);
            // 更新模式只用來產生基準檔；若被誤設在 CI，必須失敗，不能靠改寫基準檔而永遠通過。
            Assert.Fail($"已改寫基準檔（{UpdateEnvironmentVariable}=1）。取消該環境變數後重跑以驗證。");
        }

        var baselineLines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Startup", BaselineFileName))
            .Where(line => line.Length > 0)
            .Order(StringComparer.Ordinal)
            .ToList();

        var missing = Subtract(baselineLines, actualLines);
        var unexpected = Subtract(actualLines, baselineLines);

        (missing.Count + unexpected.Count).Should().Be(
            0,
            "DI 圖與基準檔不一致。缺少的項目：{0}；多出的項目：{1}",
            string.Join(" || ", missing),
            string.Join(" || ", unexpected));
    }

    private static List<string> CaptureProjectServiceRegistrations()
    {
        IServiceCollection? capturedServices = null;

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:Issuer"] = "ProjectC.Tests",
                    ["Jwt:Audience"] = "ProjectC.Tests.Client",
                    ["Jwt:SigningKey"] = "integration-test-signing-key-not-for-prod-32+",
                    [$"{TicketSigningOptions.SectionName}:{nameof(TicketSigningOptions.SigningKey)}"] = new string('x', 32),
                });
            });
            builder.ConfigureServices(services => capturedServices = services);
        });

        _ = factory.Server;

        return capturedServices!
            .Select(Describe)
            .Where(IsTrackedRegistration)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    // IConnectionMultiplexer 的服務型別不含 "ProjectC"，但它的 lifetime 屬於 AddRedisServices 的搬移範圍，必須納入。
    private static bool IsTrackedRegistration(string line)
        => line.Contains("ProjectC", StringComparison.Ordinal)
           || line.Contains("StackExchange.Redis", StringComparison.Ordinal);

    private static string Describe(ServiceDescriptor descriptor)
    {
        var implementation = descriptor.IsKeyedService
            ? descriptor.KeyedImplementationType?.ToString()
                ?? (descriptor.KeyedImplementationInstance is not null ? "instance" : "factory")
            : descriptor.ImplementationType?.ToString()
                ?? (descriptor.ImplementationInstance is not null ? "instance" : "factory");

        return $"{descriptor.Lifetime} | {descriptor.ServiceType} | {implementation}";
    }

    private static List<string> Subtract(List<string> source, List<string> toRemove)
    {
        var remaining = new List<string>(source);
        foreach (var line in toRemove)
        {
            remaining.Remove(line);
        }

        return remaining;
    }

    private static string GetSourceBaselinePath([CallerFilePath] string callerFilePath = "")
        => Path.Combine(Path.GetDirectoryName(callerFilePath)!, BaselineFileName);
}
