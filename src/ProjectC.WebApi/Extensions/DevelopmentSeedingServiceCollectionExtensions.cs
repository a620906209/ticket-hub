using ProjectC.Infrastructure.Persistence.Seeding;

namespace ProjectC.WebApi.Extensions;

public static class DevelopmentSeedingServiceCollectionExtensions
{
    public static IServiceCollection AddDevelopmentSeeding(this IServiceCollection services, IConfiguration configuration)
    {
        // 本機開發展示用初始資料（event-management-organizer-scoping tasks.md 6.2），帳密由 .env 注入、未設定即略過，
        // 不需要 ValidateOnStart。Scoped：持有 ApplicationDbContext（見 CLAUDE.md DI 生命週期原則）。
        services.Configure<DevelopmentSeedOptions>(configuration.GetSection(DevelopmentSeedOptions.SectionName));
        services.AddScoped<DevelopmentDataSeeder>();

        return services;
    }
}
