using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Members;

/// <summary>design.md 決策 2：handler 以 AsNoTracking 讀出 Member，落地只靠條件式 UPDATE。若 Member 被追蹤且屬性被改，
/// 日後任何人在同一 scope 呼叫 SaveChanges 都會繞過條件式 UPDATE、以無條件 UPDATE 覆寫實名——
/// 輸掉競爭時尤其危險，因為那會把落敗者的值寫回去。</summary>
public class RealNameRegistrationChangeTrackerTests : IClassFixture<RealNameFaultInjectionWebApplicationFactory>
{
    private const string RealNameEndpoint = "/api/members/me/real-name";

    private readonly RealNameFaultInjectionWebApplicationFactory _factory;

    public RealNameRegistrationChangeTrackerTests(RealNameFaultInjectionWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<(HttpClient Client, Guid MemberId)> CreateAuthenticatedMemberAsync()
    {
        var client = _factory.CreateClient();
        var tokens = await AuthTestHelper.RegisterAndLoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var profile = await client.GetFromJsonAsync<ProfileResponse>("/api/members/me");
        return (client, profile!.Id);
    }

    [Theory]
    [InlineData(false, HttpStatusCode.OK)]
    [InlineData(true, HttpStatusCode.Conflict)]
    public async Task RegisterRealName_WhenRequestEnds_NoMemberIsModifiedInChangeTracker(bool losesRace, HttpStatusCode expectedStatus)
    {
        var (client, memberId) = await CreateAuthenticatedMemberAsync();
        if (losesRace) _factory.LostRaceMemberIds.TryAdd(memberId, 0);
        _factory.ChangeTrackerSnapshots.Clear();

        var response = await client.PutAsJsonAsync(RealNameEndpoint, new { realName = "王小明", nationalIdLast4 = "1234" });

        response.StatusCode.Should().Be(expectedStatus);
        _factory.ChangeTrackerSnapshots.Should().Contain(s => s.Path == RealNameEndpoint)
            .And.OnlyContain(s => s.Path != RealNameEndpoint || s.ModifiedMemberCount == 0);
    }

    private sealed record ProfileResponse(Guid Id);
}
