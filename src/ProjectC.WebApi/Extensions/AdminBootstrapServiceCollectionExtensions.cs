using Microsoft.Extensions.Options;
using ProjectC.Infrastructure.Persistence.Seeding;

namespace ProjectC.WebApi.Extensions;

public static class AdminBootstrapServiceCollectionExtensions
{
    public static IServiceCollection AddAdminBootstrap(this IServiceCollection services, IConfiguration configuration)
    {
        // 未設定即略過，沒有安全預設值需要保護，但設定不完整／不合法必須在啟動時失敗，
        // 故以 ResolveEagerlyValidatedOptions 強制解析（production-admin-bootstrap design.md 決策 5）。
        services.Configure<AdminBootstrapOptions>(configuration.GetSection(AdminBootstrapOptions.SectionName));
        services.AddSingleton<IValidateOptions<AdminBootstrapOptions>, AdminBootstrapOptionsValidator>();
        // Scoped：持有 ApplicationDbContext（見 CLAUDE.md DI 生命週期原則）。
        services.AddScoped<ProductionAdminBootstrapper>();

        return services;
    }
}
