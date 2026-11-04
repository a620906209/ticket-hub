using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using ProjectC.WebApi.BackgroundServices;
using ProjectC.WebApi.Extensions;

namespace ProjectC.WebApi.Tests.Startup;

// 快照測試跑在 Testing 環境，看不到 Testing 以外的背景服務註冊，因此直接測 AddBackgroundServices
// （webapi-composition-root-cleanup design.md Risks：Production／Development 必須註冊兩個、Testing 一個都不註冊）。
public class BackgroundServiceRegistrationTests
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void AddBackgroundServices_WhenNotTestingEnvironment_RegistersCleanupAndAdmissionServices(string environmentName)
    {
        var services = new ServiceCollection();

        services.AddBackgroundServices(CreateEnvironment(environmentName));

        GetHostedServiceImplementationTypes(services).Should().BeEquivalentTo(
            [typeof(ExpiredOrderCleanupService), typeof(PurchaseQueueAdmissionService)]);
    }

    [Fact]
    public void AddBackgroundServices_WhenTestingEnvironment_RegistersNoHostedService()
    {
        var services = new ServiceCollection();

        services.AddBackgroundServices(CreateEnvironment("Testing"));

        GetHostedServiceImplementationTypes(services).Should().BeEmpty();
    }

    private static IHostEnvironment CreateEnvironment(string environmentName)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(environmentName);
        return environment.Object;
    }

    private static List<Type?> GetHostedServiceImplementationTypes(IServiceCollection services)
        => services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Select(descriptor => descriptor.ImplementationType)
            .ToList();
}
