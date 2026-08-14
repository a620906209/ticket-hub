using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProjectC.Infrastructure.Persistence;

namespace ProjectC.WebApi.Tests.TestSupport;

public class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"membership-tests-{Guid.NewGuid()}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            // 沒有可連線的 Postgres，測試環境用固定、通過驗證的 Jwt/Auth 設定覆蓋 appsettings.json 的空白值。
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Issuer"] = "ProjectC.Tests",
                ["Jwt:Audience"] = "ProjectC.Tests.Client",
                ["Jwt:SigningKey"] = "integration-test-signing-key-not-for-prod-32+",
                ["Jwt:AccessTokenExpirationMinutes"] = "30",
                ["Auth:RefreshTokenExpirationDays"] = "14",
                ["Auth:PasswordResetTokenExpirationMinutes"] = "15",
            });
        });

        builder.ConfigureServices(services =>
        {
            // 移除 Program.cs 原本註冊的 Npgsql 設定（含 DbContextOptions 本身與底層的 IDbContextOptionsConfiguration），
            // 否則 Npgsql 與 InMemory 兩個 provider 的設定會同時套用到同一個 DbContextOptions，導致 EF Core 拋出例外。
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ApplicationDbContext>>();
            services.RemoveAll<ApplicationDbContext>();

            // 無法連線真實 Postgres 時，以 EF Core InMemory 提供者作為整合測試的替代資料庫（見完成通知中的說明）。
            services.AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase(_databaseName));
        });
    }
}
