using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Infrastructure.Persistence;

namespace ProjectC.WebApi.Tests.TestSupport;

/// <summary>在真實 DbContext 上掛 <see cref="QueryCountingInterceptor"/>，其餘註冊與 <see cref="CustomWebApplicationFactory"/> 相同
/// （order-display-enrichment tasks.md 1.3）。</summary>
public sealed class QueryCountingWebApplicationFactory : CustomWebApplicationFactory
{
    public QueryCountingInterceptor QueryCounter => Services.GetRequiredService<QueryCountingInterceptor>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
        {
            services.AddHttpContextAccessor();
            services.AddSingleton<QueryCountingInterceptor>();
            services.ConfigureDbContext<ApplicationDbContext>((sp, options) =>
                options.AddInterceptors(sp.GetRequiredService<QueryCountingInterceptor>()));
        });
    }
}
