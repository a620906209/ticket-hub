using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ProjectC.WebApi.Tests.TestSupport;

// TestServer 對所有請求回報固定的 RemoteIpAddress，無法模擬「來自受信任代理」與「來自非信任來源」。
// 在整個 pipeline 的最前面（早於 UseForwardedHeaders）插入測試 middleware，依測試專用標頭設定
// Connection.RemoteIpAddress，不在正式碼暴露任何開關（reverse-proxy-forwarded-headers design.md 決策 7）。
public class ForwardedHeadersWebApplicationFactory : CustomWebApplicationFactory
{
    public const string ConnectionIpHeader = "X-Test-Connection-Ip";
    public const string ProbePath = "/api/test-support/client-address";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddControllers().AddApplicationPart(typeof(ClientAddressProbeController).Assembly);
            services.AddTransient<IStartupFilter, SimulatedConnectionAddressStartupFilter>();
        });
    }

    public static HttpClient CreateNonRedirectingClient(WebApplicationFactory<Program> factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("http://localhost"),
        });

    public static HttpRequestMessage CreateRequest(
        string path,
        string connectionIp,
        string? forwardedFor = null,
        string? forwardedProto = null,
        string? forwardedHost = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation(ConnectionIpHeader, connectionIp);
        if (forwardedFor is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        }

        if (forwardedProto is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", forwardedProto);
        }

        if (forwardedHost is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-Host", forwardedHost);
        }

        return request;
    }

    private sealed class SimulatedConnectionAddressStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
            => app =>
            {
                app.Use((context, nextMiddleware) =>
                {
                    if (context.Request.Headers.TryGetValue(ConnectionIpHeader, out var simulatedAddress))
                    {
                        context.Connection.RemoteIpAddress = IPAddress.Parse(simulatedAddress.ToString());
                    }

                    return nextMiddleware();
                });
                next(app);
            };
    }
}

public sealed record ClientAddressProbeResponse(string? RemoteIpAddress, string Scheme, string Host);

[ApiController]
[Route("api/test-support/client-address")]
[AllowAnonymous]
public class ClientAddressProbeController : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
        => Ok(new ClientAddressProbeResponse(
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Scheme,
            Request.Host.Value));
}
