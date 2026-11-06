using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ProjectC.Application.Common;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Infrastructure.Security;
using ProjectC.WebApi.Common;

namespace ProjectC.WebApi.Extensions;

public static class AuthenticationServiceCollectionExtensions
{
    public static IServiceCollection AddAuthenticationAndAuthorization(this IServiceCollection services, IConfiguration configuration)
    {
        // JwtOptions：啟動時驗證，SigningKey 等缺失直接讓應用程式啟動失敗（Fail Fast，見 design.md 決策 9）。
        services
            .AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.Configure<AuthOptions>(configuration.GetSection("Auth"));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<AuthOptions>>().Value);

        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
        services.AddTransient<IPasswordHasher, BCryptPasswordHasher>();
        services.AddTransient<ITokenService, JwtTokenService>();

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;

                var jwtSection = configuration.GetSection(JwtOptions.SectionName);
                var jwtOptions = jwtSection.Get<JwtOptions>() ?? new JwtOptions();

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwtOptions.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwtOptions.Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
                    ClockSkew = TimeSpan.FromSeconds(30),
                };

                options.Events = new JwtBearerEvents
                {
                    OnChallenge = JwtBearerProblemDetailsResponses.WriteChallengeAsync,
                    OnForbidden = JwtBearerProblemDetailsResponses.WriteForbiddenAsync,
                };
            });

        // Singleton：RequireOrganizerContextHandler 無狀態、thread-safe，比照 ASP.NET Core 官方建議的
        // IAuthorizationHandler 註冊慣例（本次僅定義 Policy，不套用至任何既有 Controller，見 4.3a）。
        services.AddSingleton<IAuthorizationHandler, RequireOrganizerContextHandler>();

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthorizationPolicies.AdminOnly, policy => policy.RequireRole("Admin"))
            .AddPolicy(AuthorizationPolicies.RequireOrganizerContext, policy => policy.Requirements.Add(new RequireOrganizerContextRequirement()));

        return services;
    }
}
