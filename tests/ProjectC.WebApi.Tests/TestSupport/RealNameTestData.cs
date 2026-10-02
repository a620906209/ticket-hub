using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using ProjectC.Application.Members;

namespace ProjectC.WebApi.Tests.TestSupport;

/// <summary>
/// real-name-verification 整合測試共用的種子資料。一律走真實 HTTP 端點建立（含 <c>PUT /api/members/me/real-name</c>），
/// 讓閘門讀到的是真正落地的資料，而不是測試直接寫 DB 繞過條件式 UPDATE 的結果。
/// </summary>
public static class RealNameTestData
{
    public sealed record SeededMember(HttpClient Client, Guid MemberId);

    public static async Task<SeededMember> CreateMemberAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var tokens = await AuthTestHelper.RegisterAndLoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var profile = await client.GetFromJsonAsync<MemberProfileDto>("/api/members/me");
        return new SeededMember(client, profile!.Id);
    }

    public static async Task<SeededMember> CreateRegisteredMemberAsync(
        WebApplicationFactory<Program> factory, string realName = "王小明", string nationalIdLast4 = "1234")
    {
        var member = await CreateMemberAsync(factory);
        var response = await member.Client.PutAsJsonAsync("/api/members/me/real-name", new { realName, nationalIdLast4 });
        response.EnsureSuccessStatusCode();
        return member;
    }
}
