using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProjectC.Infrastructure.Persistence;

namespace ProjectC.WebApi.Tests.TestSupport;

// 共用邏輯抽出來讓 CustomWebApplicationFactory 與 LoginRateLimitedWebApplicationFactory 都能呼叫，
// 兩者用組合而非繼承共用這段設定（CLAUDE.md「優先組合而非繼承」；login-rate-limiting design.md
// 決策 6 第 3 點——LoginRateLimitedWebApplicationFactory 不繼承 CustomWebApplicationFactory，避免
// 連帶繼承它「建構子直接建立一個 Testcontainers 容器物件」的既有行為）。
internal static class TestHostConfiguration
{
    public static void ApplyCommonTestConfiguration(IWebHostBuilder builder, string connectionString)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            // 沒有真正的 JWT 設定來源（appsettings.json 留空），測試環境用固定、通過驗證的值覆蓋。
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Issuer"] = "ProjectC.Tests",
                ["Jwt:Audience"] = "ProjectC.Tests.Client",
                ["Jwt:SigningKey"] = "integration-test-signing-key-not-for-prod-32+",
                ["Jwt:AccessTokenExpirationMinutes"] = "30",
                ["Auth:RefreshTokenExpirationDays"] = "14",
                ["Auth:PasswordResetTokenExpirationMinutes"] = "15",
                // TicketSigningOptions 有 ValidateOnStart（見 Program.cs），appsettings.json 留空，
                // 測試環境一樣要用固定、通過驗證的值覆蓋，否則 host build 會直接失敗（比照上面的 Jwt 設定）。
                ["TicketSigning:SigningKey"] = "integration-test-ticket-signing-key-not-for-prod-32+",
            });
        });

        builder.ConfigureServices(services =>
        {
            // 移除 Program.cs 原本指向 appsettings 連線字串的 Npgsql 設定（含底層的 IDbContextOptionsConfiguration），
            // 換成指向 Testcontainers 啟動的 Postgres，否則兩份 provider 設定會同時套用而讓 EF Core 拋例外。
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ApplicationDbContext>>();
            services.RemoveAll<ApplicationDbContext>();

            services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(connectionString));
        });
    }
}
