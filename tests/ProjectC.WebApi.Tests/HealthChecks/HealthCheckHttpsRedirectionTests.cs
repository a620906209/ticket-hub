using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using ProjectC.WebApi.Tests.TestSupport;
using static ProjectC.WebApi.Tests.HealthChecks.HealthEndpointTestSupport;

namespace ProjectC.WebApi.Tests.HealthChecks;

// 平台探針以 HTTP 直連 Pod，且把 3xx 視同成功或不跟隨轉址；被轉址後探測結果就不再反映實際狀態。
public class HealthCheckHttpsRedirectionTests
{
    private static Task RunWithHttpClientAsync(Func<HttpClient, Task> runTestAsync)
        => RunWithFactoryAsync(
            factory => factory.HttpsPort = "443",
            factory => runTestAsync(factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("http://localhost"),
            })));

    // HC-NO-REDIRECT
    [Theory]
    [InlineData(LivePath)]
    [InlineData(ReadyPath)]
    public async Task HealthEndpoints_WhenHttpsPortConfigured_DoNotRedirect(string path)
    {
        await RunWithHttpClientAsync(async client =>
        {
            var response = await client.GetAsync(path);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Headers.Location.Should().BeNull();
        });
    }

    // HC-NO-REDIRECT-CASE-INSENSITIVE：路由不分大小寫，排除條件若分大小寫，同一端點會時而被轉址。
    [Fact]
    public async Task GetHealthLive_WhenPathCaseDiffers_DoesNotRedirect()
    {
        await RunWithHttpClientAsync(async client =>
        {
            var response = await client.GetAsync("/Health/Live");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Headers.Location.Should().BeNull();
        });
    }

    // HC-REDIRECT-OTHER-UNCHANGED：同時證明 HTTPS_PORT 確實生效，否則上面的「不轉址」沒有意義。
    [Fact]
    public async Task GetEvents_WhenHttpsPortConfigured_Redirects()
    {
        await RunWithHttpClientAsync(async client =>
        {
            var response = await client.GetAsync("/api/events");

            ((int)response.StatusCode).Should().BeInRange(300, 399);
        });
    }
}
