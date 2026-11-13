using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProjectC.Infrastructure.Persistence;
using ProjectC.WebApi.Extensions;

namespace ProjectC.WebApi.Tests.TestSupport;

// 共用的 TestHostConfiguration 會把正式的 DbContext 註冊換成直接指向 Testcontainers 的 UseNpgsql，
// 使 Database:* 設定不生效。本 factory 沿用 CustomWebApplicationFactory 的容器，但在其之後移除測試 helper 的
// 註冊並重新呼叫正式的 AddApplicationDbContext()，讓測試驗證的是正式註冊碼而非測試自己的複本
// （db-timeout-pool-tuning design.md 決策 2）。不修改共用 TestHostConfiguration，不波及既有整合測試。
public class DatabaseSettingsWebApplicationFactory : CustomWebApplicationFactory
{
    public string DatabaseConnectionString => TestDatabaseConnectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = TestDatabaseConnectionString,
            });
        });

        builder.ConfigureServices(services =>
        {
            // 與 TestHostConfiguration 相同的三個 RemoveAll；少任何一個都會讓兩份 provider 設定同時套用而拋例外。
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ApplicationDbContext>>();
            services.RemoveAll<ApplicationDbContext>();

            services.AddApplicationDbContext();
        });
    }
}
