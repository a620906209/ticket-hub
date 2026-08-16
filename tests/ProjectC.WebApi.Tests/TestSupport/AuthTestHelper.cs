using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Application.Authentication;
using ProjectC.Application.Authentication.Login;
using ProjectC.Application.Members.Register;
using ProjectC.Domain.Members;
using ProjectC.Infrastructure.Persistence;

namespace ProjectC.WebApi.Tests.TestSupport;

public static class AuthTestHelper
{
    public const string DefaultPassword = "Password123";

    public static string NewEmail() => $"user-{Guid.NewGuid():N}@example.com";

    public static async Task RegisterAsync(HttpClient client, string email, string password = DefaultPassword, string displayName = "Test User")
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterMemberRequest(email, password, displayName));
        response.EnsureSuccessStatusCode();
    }

    public static async Task<AuthTokensDto> LoginAsync(HttpClient client, string email, string password = DefaultPassword)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthTokensDto>())!;
    }

    public static async Task<AuthTokensDto> RegisterAndLoginAsync(HttpClient client, string? email = null, string password = DefaultPassword)
    {
        email ??= NewEmail();
        await RegisterAsync(client, email, password);
        return await LoginAsync(client, email, password);
    }

    /// <summary>
    /// 目前 Domain 沒有公開的「指派角色」流程（角色指派非本次 spec 範圍），
    /// 測試以 EF Core ChangeTracker 直接改寫私有 setter 的 Role 欄位來模擬既有 Admin 帳號。
    /// </summary>
    public static async Task PromoteToAdminAsync(IServiceProvider services, string email)
    {
        using var scope = services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var member = await dbContext.Members.SingleAsync(m => m.Email == email);
        dbContext.Entry(member).Property(m => m.Role).CurrentValue = MemberRole.Admin;
        await dbContext.SaveChangesAsync();
    }

    /// <summary>註冊一個新會員、升為 Admin，回傳已帶好 Bearer Token 的 HttpClient。</summary>
    public static async Task<HttpClient> CreateAuthenticatedAdminClientAsync(CustomWebApplicationFactory factory, string? email = null)
    {
        email ??= NewEmail();
        await RegisterAsync(factory.CreateClient(), email);
        await PromoteToAdminAsync(factory.Services, email);

        var adminClient = factory.CreateClient();
        var tokens = await LoginAsync(adminClient, email);
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return adminClient;
    }
}
