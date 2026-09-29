using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Application.Authentication;
using ProjectC.Application.Authentication.Login;
using ProjectC.Application.Members.Register;
using ProjectC.Application.Organizers.SwitchOrganizerContext;
using ProjectC.Domain.Members;
using ProjectC.Domain.Organizers;
using ProjectC.Infrastructure.Persistence;

namespace ProjectC.WebApi.Tests.TestSupport;

public static class AuthTestHelper
{
    public const string DefaultPassword = "Password123";

    public static string NewEmail() => $"user-{Guid.NewGuid():N}@example.com";

    public static async Task RegisterAsync(HttpClient client, string email, string password = DefaultPassword, string displayName = "Test User")
    {
        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterMemberRequest(email, password, displayName, FakeCaptchaService.ValidToken, FakeCaptchaService.ValidAnswer));
        response.EnsureSuccessStatusCode();
    }

    public static async Task<AuthTokensDto> LoginAsync(HttpClient client, string email, string password = DefaultPassword)
    {
        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(email, password, FakeCaptchaService.ValidToken, FakeCaptchaService.ValidAnswer));
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

    /// <summary>
    /// 直接以 EF Core ChangeTracker 建立一筆已 Approved 的 Organizer 與對應 Owner 成員關聯——
    /// 略過申請／審核工作流，供只需要「已切換至一個 Approved Organizer」前置條件的測試重用
    /// （見 organizer-management tasks.md 6.1，供後續依賴本次的變更，例如
    /// event-management-organizer-scoping，直接呼叫）。
    /// </summary>
    public static async Task<Guid> CreateApprovedOrganizerAsync(IServiceProvider services, Guid memberId, string name = "Test Organizer")
    {
        using var scope = services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var organizer = Organizer.Apply(Guid.NewGuid(), name, memberId, DateTime.UtcNow);
        organizer.Approve(memberId, DateTime.UtcNow);
        dbContext.Organizers.Add(organizer);
        dbContext.OrganizerMembers.Add(new OrganizerMember(Guid.NewGuid(), organizer.Id, memberId, OrganizerMemberRole.Owner));
        await dbContext.SaveChangesAsync();

        return organizer.Id;
    }

    /// <summary>
    /// 註冊一個新會員、直接建立一個已 Approved 的 Organizer（該會員為 Owner），呼叫真實的切換操作
    /// 情境端點換發帶 <c>OrganizerId</c> claim 的 Access Token，回傳已帶好 Bearer Token 的 HttpClient
    /// 與該 Organizer 的 Id。
    /// </summary>
    public static async Task<(HttpClient Client, Guid OrganizerId)> CreateAuthenticatedApprovedOrganizerClientAsync(
        WebApplicationFactory<Program> factory,
        string? email = null,
        string organizerName = "Test Organizer")
    {
        email ??= NewEmail();
        await RegisterAsync(factory.CreateClient(), email);
        return await SwitchToNewApprovedOrganizerAsync(factory, email, organizerName);
    }

    /// <summary>
    /// 比照部署回填後既有 Admin 的實際狀態（event-management-organizer-scoping Migration Plan：既有 Admin
    /// 皆成為轉入用 Organizer 的 Owner，切換一次即可恢復操作）：同時帶 Admin 角色與 <c>OrganizerId</c> claim。
    /// 供需要同時具備 Admin 角色與 Organizer 情境的既有測試使用（活動、訂單、核銷、銷售報表、熱門搶購模式開關皆已改為
    /// RequireOrganizerContext，見 order-report-redemption-organizer-scoping、purchase-queue-organizer-scoping）。
    /// </summary>
    public static async Task<HttpClient> CreateAuthenticatedAdminWithOrganizerContextClientAsync(WebApplicationFactory<Program> factory)
    {
        var email = NewEmail();
        await RegisterAsync(factory.CreateClient(), email);
        await PromoteToAdminAsync(factory.Services, email);
        var (client, _) = await SwitchToNewApprovedOrganizerAsync(factory, email, "Test Organizer");
        return client;
    }

    private static async Task<(HttpClient Client, Guid OrganizerId)> SwitchToNewApprovedOrganizerAsync(
        WebApplicationFactory<Program> factory, string email, string organizerName)
    {
        var tokens = await LoginAsync(factory.CreateClient(), email);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var member = await dbContext.Members.SingleAsync(m => m.Email == email);
        var organizerId = await CreateApprovedOrganizerAsync(factory.Services, member.Id, organizerName);

        var switchClient = factory.CreateClient();
        switchClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var switchResponse = await switchClient.PostAsJsonAsync(
            $"/api/organizers/{organizerId}/switch-context",
            new SwitchOrganizerContextRequest(tokens.RefreshToken));
        switchResponse.EnsureSuccessStatusCode();
        var switchResult = (await switchResponse.Content.ReadFromJsonAsync<SwitchOrganizerContextResultDto>())!;

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", switchResult.AccessToken);
        return (client, organizerId);
    }
}
