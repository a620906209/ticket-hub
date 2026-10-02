using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Members;
using ProjectC.Infrastructure.Persistence;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Members;

public class MembersControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public MembersControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync(string email)
    {
        var client = _factory.CreateClient();
        var tokens = await AuthTestHelper.RegisterAndLoginAsync(client, email);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    [Fact]
    public async Task GetMe_WithValidToken_ReturnsOwnProfile()
    {
        var email = AuthTestHelper.NewEmail();
        var client = await CreateAuthenticatedClientAsync(email);

        var response = await client.GetAsync("/api/members/me");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var profile = await response.Content.ReadFromJsonAsync<MemberProfileResponse>();
        profile!.Email.Should().Be(email);
        profile.Role.Should().Be("Member");
        profile.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task GetMe_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/members/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UpdateMe_WithValidDisplayName_UpdatesProfile()
    {
        var client = await CreateAuthenticatedClientAsync(AuthTestHelper.NewEmail());

        var response = await client.PutAsJsonAsync("/api/members/me", new { displayName = "Updated Name" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var profile = await response.Content.ReadFromJsonAsync<MemberProfileResponse>();
        profile!.DisplayName.Should().Be("Updated Name");
    }

    [Fact]
    public async Task UpdateMe_WithExtraRoleFieldInBody_IgnoresRoleAndKeepsMember()
    {
        var client = await CreateAuthenticatedClientAsync(AuthTestHelper.NewEmail());

        // UpdateMyProfileRequest 只有 DisplayName 屬性，多送的 role 欄位在模型繫結時會被忽略。
        var response = await client.PutAsJsonAsync("/api/members/me", new { displayName = "Still Member", role = "Admin" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var profile = await response.Content.ReadFromJsonAsync<MemberProfileResponse>();
        profile!.Role.Should().Be("Member");
    }

    [Fact]
    public async Task UpdateMe_WithEmptyDisplayName_Returns400()
    {
        var client = await CreateAuthenticatedClientAsync(AuthTestHelper.NewEmail());

        var response = await client.PutAsJsonAsync("/api/members/me", new { displayName = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- 實名登記（real-name-verification）----

    private const string RealNameEndpoint = "/api/members/me/real-name";

    private async Task<(HttpClient Client, Guid MemberId)> CreateAuthenticatedMemberAsync()
    {
        var client = await CreateAuthenticatedClientAsync(AuthTestHelper.NewEmail());
        var profile = await client.GetFromJsonAsync<MemberProfileResponse>("/api/members/me");
        return (client, profile!.Id);
    }

    private async Task<(string? RealName, string? NationalIdLast4)> GetStoredRealNameAsync(Guid memberId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var member = await dbContext.Members.AsNoTracking().SingleAsync(m => m.Id == memberId);
        return (member.RealName, member.NationalIdLast4);
    }

    private static object RealNameBody(string realName, string nationalIdLast4) => new { realName, nationalIdLast4 };

    private static void AssertNoStore(HttpResponseMessage response)
        => response.Headers.CacheControl!.NoStore.Should().BeTrue();

    // RNV-REGISTER-001：完整末四碼只能出現在查詢持票人端點，登記回應也只能是遮蔽值。
    [Fact]
    public async Task RegisterRealName_WhenNotRegistered_Returns200WithMaskedLast4Only()
    {
        var (client, memberId) = await CreateAuthenticatedMemberAsync();

        var response = await client.PutAsJsonAsync(RealNameEndpoint, RealNameBody("王小明", "1234"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        HexIdentifierText.RemoveHexIdentifiers(json).Should().NotContain("1234");
        var profile = JsonSerializer.Deserialize<MemberProfileResponse>(json, JsonSerializerOptions.Web)!;
        profile.HasRegisteredRealName.Should().BeTrue();
        profile.RealName.Should().Be("王小明");
        profile.NationalIdLast4Masked.Should().Be("**34");
        (await GetStoredRealNameAsync(memberId)).Should().Be(("王小明", "1234"));
    }

    // RNV-REGISTER-002／RNV-REGISTER-003／RNV-ERROR-001：實名不可改；相同值也回 409，否則客戶端會誤以為重送可覆寫。
    // 409 body 不得含已登記或本次送出的值，因為 ProblemDetails 會被前端顯示或記錄。
    [Theory]
    [InlineData("李大華", "5678")]
    [InlineData("王小明", "1234")]
    public async Task RegisterRealName_WhenAlreadyRegistered_Returns409WithoutChangingOrLeakingRealName(string realName, string nationalIdLast4)
    {
        var (client, memberId) = await CreateAuthenticatedMemberAsync();
        (await client.PutAsJsonAsync(RealNameEndpoint, RealNameBody("王小明", "1234"))).EnsureSuccessStatusCode();

        var response = await client.PutAsJsonAsync(RealNameEndpoint, RealNameBody(realName, nationalIdLast4));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadAsStringAsync();
        HexIdentifierText.RemoveHexIdentifiers(body).Should().NotContain("王小明").And.NotContain("1234").And.NotContain(realName).And.NotContain(nationalIdLast4);
        (await GetStoredRealNameAsync(memberId)).Should().Be(("王小明", "1234"));
    }

    // RNV-REGISTER-004（端到端）：條件式 UPDATE 保證並發的兩個首次登記只有一個生效，且 DB 兩欄來自同一個請求。
    [Fact]
    public async Task RegisterRealName_WhenTwoConcurrentRequestsWithDifferentValues_ExactlyOneWinsAndColumnsAreConsistent()
    {
        var (client, memberId) = await CreateAuthenticatedMemberAsync();

        var responses = await Task.WhenAll(
            client.PutAsJsonAsync(RealNameEndpoint, RealNameBody("王小明", "1111")),
            client.PutAsJsonAsync(RealNameEndpoint, RealNameBody("李大華", "2222")));

        responses.Select(r => r.StatusCode).Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict]);
        var winner = responses[0].StatusCode == HttpStatusCode.OK ? ("王小明", "1111") : ("李大華", "2222");
        (await GetStoredRealNameAsync(memberId)).Should().Be(winner);
    }

    // RNV-REGISTER-005
    [Fact]
    public async Task RegisterRealName_WithoutToken_Returns401()
    {
        var response = await _factory.CreateClient().PutAsJsonAsync(RealNameEndpoint, RealNameBody("王小明", "1234"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // RNV-REGISTER-006：Token 合法但會員已不存在（例如被刪除）時不得建立任何資料。
    [Fact]
    public async Task RegisterRealName_WhenTokenMemberDoesNotExist_Returns404WithoutWriting()
    {
        var client = _factory.CreateClient();
        var ghost = Member.Register(AuthTestHelper.NewEmail(), "Ghost", "unused-hash");
        using (var scope = _factory.Services.CreateScope())
        {
            var accessToken = scope.ServiceProvider.GetRequiredService<ITokenService>().GenerateAccessToken(ghost);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        var response = await client.PutAsJsonAsync(RealNameEndpoint, RealNameBody("王小明", "1234"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var verifyScope = _factory.Services.CreateScope();
        var dbContext = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await dbContext.Members.AnyAsync(m => m.Id == ghost.Id)).Should().BeFalse();
    }

    // RNV-FORMAT-003：現場比對證件時，前後空白不得造成不一致。
    [Fact]
    public async Task RegisterRealName_WhenRealNameHasSurroundingWhitespace_StoresTrimmedValue()
    {
        var (client, memberId) = await CreateAuthenticatedMemberAsync();

        var response = await client.PutAsJsonAsync(RealNameEndpoint, RealNameBody("  王小明  ", "1234"));

        (await response.Content.ReadFromJsonAsync<MemberProfileResponse>())!.RealName.Should().Be("王小明");
        (await GetStoredRealNameAsync(memberId)).RealName.Should().Be("王小明");
    }

    // RNV-FORMAT-001／002／004／005：實名寫入後不可改，格式錯誤必須在寫入前擋下。
    [Theory]
    [InlineData("", "1234")]
    [InlineData("   ", "1234")]
    [InlineData("王\n小明", "1234")]
    [InlineData("王小明", "123")]
    [InlineData("王小明", "12345")]
    [InlineData("王小明", "12a4")]
    [InlineData("王小明", "１２３４")]
    [InlineData("王小明", "12 4")]
    public async Task RegisterRealName_WhenFormatInvalid_Returns400ValidationWithoutWriting(string realName, string nationalIdLast4)
    {
        var (client, memberId) = await CreateAuthenticatedMemberAsync();

        var response = await client.PutAsJsonAsync(RealNameEndpoint, RealNameBody(realName, nationalIdLast4));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Title.Should().Be("Validation");
        (await GetStoredRealNameAsync(memberId)).Should().Be(((string?)null, (string?)null));
    }

    // RNV-FORMAT-002：上限以 trim 後的 50 字計算，邊界兩側都要驗。
    [Fact]
    public async Task RegisterRealName_WhenTrimmedRealNameIs51Characters_Returns400WithoutWriting()
    {
        var (client, memberId) = await CreateAuthenticatedMemberAsync();

        var response = await client.PutAsJsonAsync(RealNameEndpoint, RealNameBody($" {new string('王', 51)} ", "1234"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Title.Should().Be("Validation");
        (await GetStoredRealNameAsync(memberId)).Should().Be(((string?)null, (string?)null));
    }

    [Fact]
    public async Task RegisterRealName_WhenRealNameIsExactly50Characters_Returns200AndStoresFullValue()
    {
        var (client, memberId) = await CreateAuthenticatedMemberAsync();
        var realName = new string('王', 50);

        var response = await client.PutAsJsonAsync(RealNameEndpoint, RealNameBody(realName, "1234"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetStoredRealNameAsync(memberId)).Should().Be((realName, "1234"));
    }

    // RNV-FORMAT-006：400 回應不得回顯輸入值。
    [Fact]
    public async Task RegisterRealName_WhenNationalIdLast4Invalid_ResponseDoesNotEchoInput()
    {
        var (client, _) = await CreateAuthenticatedMemberAsync();

        var response = await client.PutAsJsonAsync(RealNameEndpoint, RealNameBody("王小明", "98x7"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("98x7");
    }

    // RNV-NOSTORE-003：登記端點的所有回應都可能含個資或其線索，三種結果都不得被快取。
    [Fact]
    public async Task RegisterRealName_SuccessConflictAndValidationResponses_AllHaveNoStore()
    {
        var (client, _) = await CreateAuthenticatedMemberAsync();

        var success = await client.PutAsJsonAsync(RealNameEndpoint, RealNameBody("王小明", "1234"));
        var conflict = await client.PutAsJsonAsync(RealNameEndpoint, RealNameBody("王小明", "1234"));
        var validation = await client.PutAsJsonAsync(RealNameEndpoint, RealNameBody("王小明", "12a4"));

        success.StatusCode.Should().Be(HttpStatusCode.OK);
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        validation.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        AssertNoStore(success);
        AssertNoStore(conflict);
        AssertNoStore(validation);
    }

    // MM-PROFILE-RN-001／RNV-NOSTORE-002
    [Fact]
    public async Task GetMe_WhenRealNameNotRegistered_ReturnsNullRealNameFieldsWithNoStore()
    {
        var (client, _) = await CreateAuthenticatedMemberAsync();

        var response = await client.GetAsync("/api/members/me");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertNoStore(response);
        var profile = (await response.Content.ReadFromJsonAsync<MemberProfileResponse>())!;
        profile.HasRegisteredRealName.Should().BeFalse();
        profile.RealName.Should().BeNull();
        profile.NationalIdLast4Masked.Should().BeNull();
    }

    // MM-PROFILE-RN-002／RNV-MASK-002／RNV-NOSTORE-001
    [Fact]
    public async Task GetMe_WhenRealNameRegistered_ReturnsFullNameAndMaskedLast4WithNoStore()
    {
        var (client, _) = await CreateAuthenticatedMemberAsync();
        (await client.PutAsJsonAsync(RealNameEndpoint, RealNameBody("王小明", "1234"))).EnsureSuccessStatusCode();

        var response = await client.GetAsync("/api/members/me");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertNoStore(response);
        var json = await response.Content.ReadAsStringAsync();
        HexIdentifierText.RemoveHexIdentifiers(json).Should().NotContain("1234");
        var profile = JsonSerializer.Deserialize<MemberProfileResponse>(json, JsonSerializerOptions.Web)!;
        profile.RealName.Should().Be("王小明");
        profile.NationalIdLast4Masked.Should().Be("**34");
    }

    // MM-UPDATE-RN-001：一般個人資料更新不得成為繞過「實名不可改」的後門。
    [Fact]
    public async Task UpdateMe_WithRealNameFieldsInBody_UpdatesDisplayNameOnlyAndKeepsRealName()
    {
        var (client, memberId) = await CreateAuthenticatedMemberAsync();
        (await client.PutAsJsonAsync(RealNameEndpoint, RealNameBody("王小明", "1234"))).EnsureSuccessStatusCode();

        var response = await client.PutAsJsonAsync(
            "/api/members/me", new { displayName = "New Name", realName = "李大華", nationalIdLast4 = "5678" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<MemberProfileResponse>())!.DisplayName.Should().Be("New Name");
        (await GetStoredRealNameAsync(memberId)).Should().Be(("王小明", "1234"));
    }

    // RNV-NOSTORE-006：更新顯示名稱的回應同樣帶出姓名與遮蔽末四碼，不得被瀏覽器或中介快取保存。
    [Fact]
    public async Task UpdateMe_WhenRealNameRegistered_ReturnsRealNameWithNoStore()
    {
        var (client, _) = await CreateAuthenticatedMemberAsync();
        (await client.PutAsJsonAsync(RealNameEndpoint, RealNameBody("王小明", "1234"))).EnsureSuccessStatusCode();

        var response = await client.PutAsJsonAsync("/api/members/me", new { displayName = "New Name" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<MemberProfileResponse>())!.RealName.Should().Be("王小明");
        AssertNoStore(response);
    }

    private sealed record MemberProfileResponse(
        Guid Id, string Email, string DisplayName, string Role, bool IsActive,
        bool HasRegisteredRealName = false, string? RealName = null, string? NationalIdLast4Masked = null);
}
