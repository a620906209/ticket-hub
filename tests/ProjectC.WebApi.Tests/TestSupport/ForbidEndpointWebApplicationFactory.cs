using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ProjectC.WebApi.Tests.TestSupport;

// 正式控制器內的 Forbid() 受 Policy 保護而無法經 HTTP 觸發，AER-016 改註冊測試專用端點驗證
// （jwt-challenge-problem-details design.md 決策 4）。
public class ForbidEndpointWebApplicationFactory : CustomWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
            services.AddControllers().AddApplicationPart(typeof(ForbidTestController).Assembly));
    }
}

[ApiController]
[Route("api/test-support/forbid")]
[Authorize]
public class ForbidTestController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Forbid();
}
