using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Startup;

// host-header-validation spec HHV-001～003。框架 HostFiltering 的特性測試：防的是「部署設定了明確 AllowedHosts
// 卻因日後管線或設定改動而失效，偽造 Host 的請求被放行」，以及「預設 `*` 被誤改而擋掉開發環境」。
public class HostFilteringTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string ConfiguredAllowedHosts = "app.example";
    private const string ForgedHost = "evil.example";

    private readonly CustomWebApplicationFactory _factory;

    public HostFilteringTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient CreateClientWithAllowedHosts(string allowedHosts)
        => _factory
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configBuilder) =>
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = allowedHosts })))
            .CreateClient();

    private static async Task<HttpStatusCode> GetStatusWithHostAsync(HttpClient client, string path, string host)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Host = host;
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    // HHV-001
    [Fact]
    public async Task GetHealthLive_WhenHostNotAllowed_Returns400()
    {
        using var client = CreateClientWithAllowedHosts(ConfiguredAllowedHosts);

        var status = await GetStatusWithHostAsync(client, "/health/live", ForgedHost);

        status.Should().Be(HttpStatusCode.BadRequest);
    }

    // HHV-001
    [Fact]
    public async Task GetApiPath_WhenHostNotAllowed_Returns400()
    {
        using var client = CreateClientWithAllowedHosts(ConfiguredAllowedHosts);

        var status = await GetStatusWithHostAsync(client, "/api/events", ForgedHost);

        status.Should().Be(HttpStatusCode.BadRequest);
    }

    // HHV-002
    [Theory]
    [InlineData("app.example")]
    [InlineData("app.example:8443")]
    public async Task GetHealthLive_WhenHostAllowed_Returns200(string host)
    {
        using var client = CreateClientWithAllowedHosts(ConfiguredAllowedHosts);

        var status = await GetStatusWithHostAsync(client, "/health/live", host);

        status.Should().Be(HttpStatusCode.OK);
    }

    // HHV-003
    [Theory]
    [InlineData("evil.example")]
    [InlineData("localhost")]
    public async Task GetHealthLive_WhenAllowedHostsIsDefault_Returns200ForAnyHost(string host)
    {
        using var client = _factory.CreateClient();

        var status = await GetStatusWithHostAsync(client, "/health/live", host);

        status.Should().Be(HttpStatusCode.OK);
    }
}
