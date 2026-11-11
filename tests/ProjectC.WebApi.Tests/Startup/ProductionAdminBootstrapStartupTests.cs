using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Domain.Members;
using ProjectC.Infrastructure.Persistence;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Startup;

// production-admin-bootstrap ADMIN-BOOT-001／004：走真實啟動流程（Program.cs 的環境判斷），
// 不是只測抽出的輔助方法——環境判斷反轉時 Development 與非 Development 的測試必須有一邊失敗。
public class ProductionAdminBootstrapStartupTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string AdminPassword = "Startup-Passw0rd";
    private readonly CustomWebApplicationFactory _factory;

    public ProductionAdminBootstrapStartupTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private WebApplicationFactory<Program> CreateConfiguredFactory(string environmentName, string adminEmail)
        => _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environmentName);
            // 以 AddInMemoryCollection 而非 UseSetting：api 容器的環境變數帶有 DevelopmentSeed__*（.env 注入），
            // 環境變數的優先序高於 UseSetting，必須以較晚加入的設定來源明確清空，Development 案例才不會被展示用 seeder 污染。
            builder.ConfigureAppConfiguration((_, configBuilder) => configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AdminBootstrap:Email"] = adminEmail,
                ["AdminBootstrap:Password"] = AdminPassword,
                ["DevelopmentSeed:AdminEmail"] = string.Empty,
                ["DevelopmentSeed:AdminPassword"] = string.Empty,
            }));
        });

    private async Task ResetMembersAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        #pragma warning disable EF1002 // 固定字串、無外部輸入；測試前清空整個 Members 關聯以確保「資料庫無任何 Admin」的前置條件。
        await dbContext.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"Members\" CASCADE");
        #pragma warning restore EF1002
    }

    private async Task<List<Member>> LoadAdminsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await dbContext.Members.AsNoTracking().Where(m => m.Role == MemberRole.Admin).ToListAsync();
    }

    [Fact]
    public async Task Startup_InDevelopmentWithBootstrapConfigured_DoesNotCreateAdmin()
    {
        await ResetMembersAsync();
        var email = AuthTestHelper.NewEmail();
        using var factory = CreateConfiguredFactory("Development", email);

        using var client = factory.CreateClient();

        (await LoadAdminsAsync()).Should().BeEmpty("Development 由既有展示用 seeder 負責，不執行此機制");
    }

    [Fact]
    public async Task Startup_InTestingWithBootstrapConfigured_CreatesAdminWhoCanLoginWithoutOrganizer()
    {
        await ResetMembersAsync();
        var email = AuthTestHelper.NewEmail();
        using var factory = CreateConfiguredFactory("Testing", email);

        using var client = factory.CreateClient();

        var admin = (await LoadAdminsAsync()).Should().ContainSingle().Which;
        admin.Email.Should().Be(email);
        admin.IsActive.Should().BeTrue();
        var tokens = await AuthTestHelper.LoginAsync(client, email, AdminPassword);
        tokens.AccessToken.Should().NotBeNullOrWhiteSpace();
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await dbContext.Organizers.CountAsync()).Should().Be(0);
        (await dbContext.OrganizerMembers.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Startup_InProductionWithBootstrapConfigured_CreatesAdmin()
    {
        await ResetMembersAsync();
        var email = AuthTestHelper.NewEmail();
        using var factory = CreateConfiguredFactory("Production", email);

        factory.Services.GetRequiredService<IConfiguration>()["Captcha:VerificationMode"]
            .Should().BeNull("Production 禁用 AcceptAny，也不得帶入 Captcha 設定");
        using var client = factory.CreateClient();

        var admin = (await LoadAdminsAsync()).Should().ContainSingle().Which;
        admin.Email.Should().Be(email);
    }

    [Fact]
    public async Task Startup_InProductionWhenAdminAlreadyExists_DoesNotCreateSecondAdmin()
    {
        await ResetMembersAsync();
        var existingEmail = AuthTestHelper.NewEmail();
        using (var firstStart = CreateConfiguredFactory("Production", existingEmail))
        {
            using var firstClient = firstStart.CreateClient();
        }

        using var secondStart = CreateConfiguredFactory("Production", AuthTestHelper.NewEmail());
        using var secondClient = secondStart.CreateClient();

        (await LoadAdminsAsync()).Should().ContainSingle().Which.Email.Should().Be(existingEmail);
    }

    // 其他既有整合測試共用的 CustomWebApplicationFactory 不得帶入 AdminBootstrap 設定：
    // Testing 環境一旦有設定就會執行 bootstrapper，會讓所有測試的資料庫多出 Admin。
    [Fact]
    public void BaseTestFactory_DoesNotConfigureAdminBootstrap()
    {
        var configuration = _factory.Services.GetRequiredService<IConfiguration>();

        configuration.GetSection("AdminBootstrap").GetChildren().Should().BeEmpty();
    }
}
