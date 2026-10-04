using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Domain.Tickets;
using ProjectC.Infrastructure.Persistence;
using ProjectC.WebApi.Tests.TestSupport;
using System.Net.Http.Json;
using ProjectC.Application.Tickets.GetTicketHolder;
using ProjectC.Application.Tickets.RedeemTicket;

namespace ProjectC.WebApi.Tests.Admin;

public class AdminTicketsControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AdminTicketsControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private Task<Guid> SeedIssuedTicketAsync(HttpClient organizerClient)
        => OrganizerScopedTestData.SeedIssuedTicketAsync(_factory, organizerClient);

    private Task<Ticket> ReadTicketAsync(Guid ticketId) => OrganizerScopedTestData.ReadTicketAsync(_factory, ticketId);

    private string SignTicket(Guid ticketId) => OrganizerScopedTestData.SignTicket(_factory, ticketId);

    private static Task<HttpResponseMessage> RedeemWithoutBodyAsync(HttpClient client, string ticketIdPathSegment)
        => client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"/api/admin/tickets/{ticketIdPathSegment}/redeem"));

    private static Task<HttpResponseMessage> RedeemAsync(HttpClient client, Guid ticketId, string? signature)
        => OrganizerScopedTestData.RedeemAsync(client, ticketId, signature);

    private static Task<HttpResponseMessage> RedeemWithRawBodyAsync(HttpClient client, Guid ticketId, string rawJsonBody)
        => client.PatchAsync(
            $"/api/admin/tickets/{ticketId}/redeem",
            new StringContent(rawJsonBody, Encoding.UTF8, "application/json"));

    private async Task AssertTicketStillIssuedAsync(Guid ticketId)
    {
        var ticket = await ReadTicketAsync(ticketId);
        ticket.Status.Should().Be(TicketStatus.Issued);
        ticket.RedeemedAtUtc.Should().BeNull();
    }

    // ---- 授權規則 ----

    // [RDM-AUTHZ-001] 非 Admin 的 Organizer 成員可核銷自家票券：帶正確簽章（掃描路徑）與不帶簽章（手動輸入路徑）
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Redeem_AsApprovedOrganizerNonAdminOnOwnTicket_Returns204AndTransitionsTicketToRedeemed(bool withSignature)
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var ticketId = await SeedIssuedTicketAsync(organizerClient);

        var response = await RedeemAsync(organizerClient, ticketId, withSignature ? SignTicket(ticketId) : null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var ticket = await ReadTicketAsync(ticketId);
        ticket.Status.Should().Be(TicketStatus.Redeemed);
        ticket.RedeemedAtUtc.Should().NotBeNull();
    }

    // [RDM-AUTHZ-002] 帶正確簽章，確保被拒絕的原因只有授權
    [Fact]
    public async Task Redeem_WithoutOrganizerContext_Returns403ForMemberAndAdminAndDoesNotChangeTicket()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var ticketId = await SeedIssuedTicketAsync(organizerClient);
        var clients = new (string Identity, HttpClient Client)[]
        {
            ("Member", await OrganizerScopedTestData.CreateAuthenticatedMemberClientAsync(_factory)),
            ("Admin", await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory)),
        };

        foreach (var (identity, client) in clients)
        {
            var response = await RedeemAsync(client, ticketId, SignTicket(ticketId));

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{identity} 未切換 Organizer");
        }

        await AssertTicketStillIssuedAsync(ticketId);
    }

    // [RDM-AUTHZ-003] 不帶 Authorization Header 與帶無效 Token 皆 401
    [Fact]
    public async Task Redeem_WithoutValidToken_Returns401AndDoesNotChangeTicket()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var ticketId = await SeedIssuedTicketAsync(organizerClient);
        var anonymousClient = _factory.CreateClient();
        var invalidTokenClient = _factory.CreateClient();
        invalidTokenClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-valid-token");

        var anonymousResponse = await RedeemAsync(anonymousClient, ticketId, SignTicket(ticketId));
        var invalidTokenResponse = await RedeemAsync(invalidTokenClient, ticketId, SignTicket(ticketId));

        anonymousResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        invalidTokenResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await AssertTicketStillIssuedAsync(ticketId);
    }

    // [RDM-AUTHZ-004] 其他 Organizer 的票券與查無此票回應逐字相同（除 ID 外）；帶簽章與不帶簽章（手動輸入路徑，6.4c）各驗一次
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Redeem_OnOtherOrganizerTicket_Returns404WithSameBodyAsMissingTicketAndDoesNotChangeTicket(bool withSignature)
    {
        var (organizerAClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var (organizerBClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var ticketB = await SeedIssuedTicketAsync(organizerBClient);
        var missingTicketId = Guid.NewGuid();

        var otherOrganizerResponse = await RedeemAsync(organizerAClient, ticketB, withSignature ? SignTicket(ticketB) : null);
        var missingResponse = await RedeemAsync(organizerAClient, missingTicketId, withSignature ? SignTicket(missingTicketId) : null);

        otherOrganizerResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        missingResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await NotFoundResponseBody.ReadNormalizedAsync(otherOrganizerResponse, ticketB))
            .Should().Be(await NotFoundResponseBody.ReadNormalizedAsync(missingResponse, missingTicketId));
        await AssertTicketStillIssuedAsync(ticketB);
    }

    // [RDM-AUTHZ-005] 其他 Organizer 已核銷的票券 MUST 回 404（不是 409），不得以核銷端點刺探票券狀態
    [Fact]
    public async Task Redeem_OnOtherOrganizerAlreadyRedeemedTicket_Returns404NotConflictAndDoesNotChangeTicket()
    {
        var (organizerAClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var (organizerBClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var ticketB = await SeedIssuedTicketAsync(organizerBClient);
        (await RedeemAsync(organizerBClient, ticketB, null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var redeemedBefore = await ReadTicketAsync(ticketB);
        var missingTicketId = Guid.NewGuid();

        var otherOrganizerResponse = await RedeemAsync(organizerAClient, ticketB, null);
        var missingResponse = await RedeemAsync(organizerAClient, missingTicketId, null);

        otherOrganizerResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await NotFoundResponseBody.ReadNormalizedAsync(otherOrganizerResponse, ticketB))
            .Should().Be(await NotFoundResponseBody.ReadNormalizedAsync(missingResponse, missingTicketId));
        var redeemedAfter = await ReadTicketAsync(ticketB);
        redeemedAfter.Status.Should().Be(TicketStatus.Redeemed);
        redeemedAfter.RedeemedAtUtc.Should().Be(redeemedBefore.RedeemedAtUtc);
    }

    // [RDM-AUTHZ-006] RequireOrganizerContext 不即時查表：停權前核發、未過期的 Access Token 在過期前仍可核銷。
    // 這是 design.md Decision 2 的既知有界延遲視窗；若此測試失敗，代表 Policy 行為改變，spec 必須同步修改。
    [Fact]
    public async Task Redeem_WithTokenIssuedBeforeOrganizerSuspended_IsStillAcceptedUntilExpiry()
    {
        var (organizerClient, organizerId) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var ticketId = await SeedIssuedTicketAsync(organizerClient);
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);

        var suspendResponse = await adminClient.PatchAsync($"/api/admin/organizers/{organizerId}/suspend", null);
        suspendResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var response = await RedeemAsync(organizerClient, ticketId, null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ReadTicketAsync(ticketId)).Status.Should().Be(TicketStatus.Redeemed);
    }

    // ---- 既有核銷規則於新授權模式下重新執行（6.5） ----

    [Fact]
    public async Task Redeem_WithoutBodyOnIssuedTicket_Returns204AndTransitionsTicketToRedeemed()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var ticketId = await SeedIssuedTicketAsync(organizerClient);

        var response = await RedeemWithoutBodyAsync(organizerClient, ticketId.ToString());

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ReadTicketAsync(ticketId)).Status.Should().Be(TicketStatus.Redeemed);
    }

    [Fact]
    public async Task Redeem_OnOwnAlreadyRedeemedTicket_Returns409AndDoesNotChangeRedeemedAt()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var ticketId = await SeedIssuedTicketAsync(organizerClient);
        (await RedeemAsync(organizerClient, ticketId, null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var redeemedAt = (await ReadTicketAsync(ticketId)).RedeemedAtUtc;

        var response = await RedeemAsync(organizerClient, ticketId, null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadTicketAsync(ticketId)).RedeemedAtUtc.Should().Be(redeemedAt);
    }

    [Fact]
    public async Task Redeem_WithNonExistentTicket_Returns404()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);

        var response = await RedeemAsync(organizerClient, Guid.NewGuid(), null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Redeem_WithNonGuidPathSegment_Returns404()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);

        var response = await RedeemWithoutBodyAsync(organizerClient, "not-a-guid");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // 對應 AC: TICKET-REDEEM-SIG-INVALID（竄改簽章回傳 400，Title 為 InvalidTicketSignature，可與其他 400 區分，且未變更 Ticket 狀態）
    [Fact]
    public async Task Redeem_WithTamperedSignature_Returns400WithInvalidTicketSignatureTitleAndDoesNotChangeTicketStatus()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var ticketId = await SeedIssuedTicketAsync(organizerClient);
        var tamperedSignature = SignTicket(ticketId) + "tampered";

        var response = await RedeemAsync(organizerClient, ticketId, tamperedSignature);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("title").GetString().Should().Be("InvalidTicketSignature");
        await AssertTicketStillIssuedAsync(ticketId);
    }

    // 對應 AC: TICKET-REDEEM-SIG-TYPE-MISMATCH（signature 帶數字型別，模型繫結失敗回傳 400 且未變更 Ticket 狀態）
    [Fact]
    public async Task Redeem_WithSignatureAsNonStringType_Returns400AndDoesNotChangeTicketStatus()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var ticketId = await SeedIssuedTicketAsync(organizerClient);

        var response = await RedeemWithRawBodyAsync(organizerClient, ticketId, """{"signature": 12345}""");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertTicketStillIssuedAsync(ticketId);
    }

    // ---- real-name-verification：需實名活動的核銷持票人確認（RDM-RN-*） ----

    private const string HolderVerificationRequiredTitle = "HolderVerificationRequired";

    private static Task<HttpResponseMessage> RedeemWithHolderFlagAsync(HttpClient client, Guid ticketId, string? signature, bool? isHolderVerified)
        => client.PatchAsJsonAsync($"/api/admin/tickets/{ticketId}/redeem", new RedeemTicketRequest(signature, isHolderVerified));

    private async Task<(HttpClient OrganizerClient, RealNameTestData.SeededTicket Ticket)> SeedRealNameTicketAsync(
        bool isRealNameRequired = true, string realName = "王小明", string nationalIdLast4 = "1234")
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var ticket = await RealNameTestData.SeedIssuedTicketAsync(_factory, organizerClient, isRealNameRequired, realName, nationalIdLast4);
        return (organizerClient, ticket);
    }

    private async Task AssertTicketRedeemedAsync(Guid ticketId)
    {
        var ticket = await ReadTicketAsync(ticketId);
        ticket.Status.Should().Be(TicketStatus.Redeemed);
        ticket.RedeemedAtUtc.Should().NotBeNull();
    }

    // [RDM-RN-001]／[RDM-RN-003]／[RNV-ERROR-001] 未明確確認證件（缺漏或 false）一律不核銷；錯誤 body 不得夾帶持票人個資。
    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task Redeem_RealNameTicketWithoutHolderVerified_Returns409HolderVerificationRequiredAndDoesNotChangeTicket(bool? isHolderVerified)
    {
        var (organizerClient, seeded) = await SeedRealNameTicketAsync(realName: "核銷錯誤測試甲", nationalIdLast4: "7531");

        var response = await RedeemWithHolderFlagAsync(organizerClient, seeded.TicketId, SignTicket(seeded.TicketId), isHolderVerified);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await RealNameTestData.ReadProblemTitleAsync(response)).Should().Be(HolderVerificationRequiredTitle);
        var body = await response.Content.ReadAsStringAsync();
        HexIdentifierText.RemoveHexIdentifiers(body).Should().NotContain("核銷錯誤測試甲").And.NotContain("7531");
        await AssertTicketStillIssuedAsync(seeded.TicketId);
    }

    // [RDM-RN-002] 掃描路徑（帶簽章）與手動輸入路徑（不帶簽章）都要能在確認後核銷。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Redeem_RealNameTicketWithHolderVerified_Returns204AndRedeemsTicket(bool withSignature)
    {
        var (organizerClient, seeded) = await SeedRealNameTicketAsync();

        var response = await RedeemWithHolderFlagAsync(
            organizerClient, seeded.TicketId, withSignature ? SignTicket(seeded.TicketId) : null, isHolderVerified: true);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await AssertTicketRedeemedAsync(seeded.TicketId);
    }

    // [RDM-RN-004] 不需實名活動維持上線前行為，舊版掃描器不帶旗標仍可核銷。
    [Fact]
    public async Task Redeem_NonRealNameTicketWithoutHolderFlag_Returns204()
    {
        var (organizerClient, seeded) = await SeedRealNameTicketAsync(isRealNameRequired: false);

        var response = await RedeemAsync(organizerClient, seeded.TicketId, SignTicket(seeded.TicketId));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await AssertTicketRedeemedAsync(seeded.TicketId);
    }

    // [RDM-RN-005] 歸屬核對先於持票人確認：不得以 409 HolderVerificationRequired 洩漏其他 Organizer 的票券存在且需實名。
    [Fact]
    public async Task Redeem_OtherOrganizerRealNameTicket_Returns404WithSameBodyAsMissingTicket()
    {
        var (_, seeded) = await SeedRealNameTicketAsync();
        var (otherOrganizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var missingTicketId = Guid.NewGuid();

        var otherOrganizerResponse = await RedeemAsync(otherOrganizerClient, seeded.TicketId, SignTicket(seeded.TicketId));
        var missingResponse = await RedeemAsync(otherOrganizerClient, missingTicketId, SignTicket(missingTicketId));

        otherOrganizerResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await NotFoundResponseBody.ReadNormalizedAsync(otherOrganizerResponse, seeded.TicketId))
            .Should().Be(await NotFoundResponseBody.ReadNormalizedAsync(missingResponse, missingTicketId));
        await AssertTicketStillIssuedAsync(seeded.TicketId);
    }

    // [RDM-RN-006] 已核銷的票不論是否帶旗標都回既有 409，現場人員才能分辨「重複入場」與「忘記確認證件」。
    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    public async Task Redeem_AlreadyRedeemedRealNameTicket_ReturnsExisting409NotHolderVerificationRequired(bool? isHolderVerified)
    {
        var (organizerClient, seeded) = await SeedRealNameTicketAsync();
        (await RedeemWithHolderFlagAsync(organizerClient, seeded.TicketId, null, isHolderVerified: true)).EnsureSuccessStatusCode();

        var response = await RedeemWithHolderFlagAsync(organizerClient, seeded.TicketId, SignTicket(seeded.TicketId), isHolderVerified);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await RealNameTestData.ReadProblemTitleAsync(response)).Should().NotBe(HolderVerificationRequiredTitle);
    }

    // [RDM-RN-007] 確認證件不得讓偽造的 QR Code 過關。
    [Fact]
    public async Task Redeem_RealNameTicketWithTamperedSignatureAndHolderVerified_ReturnsInvalidTicketSignatureAndDoesNotChangeTicket()
    {
        var (organizerClient, seeded) = await SeedRealNameTicketAsync();

        var response = await RedeemWithHolderFlagAsync(
            organizerClient, seeded.TicketId, SignTicket(seeded.TicketId) + "tampered", isHolderVerified: true);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RealNameTestData.ReadProblemTitleAsync(response)).Should().Be("InvalidTicketSignature");
        await AssertTicketStillIssuedAsync(seeded.TicketId);
    }

    // [RDM-RN-009] 只接受 JSON 布林：寬鬆轉型（"yes"、1）可能讓錯誤的掃描器設定被當成「已確認」。
    [Theory]
    [InlineData("\"yes\"")]
    [InlineData("1")]
    public async Task Redeem_WithNonBooleanHolderVerified_Returns400AndDoesNotChangeTicket(string rawValue)
    {
        var (organizerClient, seeded) = await SeedRealNameTicketAsync();

        var response = await RedeemWithRawBodyAsync(organizerClient, seeded.TicketId, $"{{\"isHolderVerified\":{rawValue}}}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertTicketStillIssuedAsync(seeded.TicketId);
    }

    // [RDM-RN-008] 確認證件後的核銷仍須遵守「只核銷一次」：兩個請求同時抵達只能有一個成功。
    [Fact]
    public async Task Redeem_ConcurrentHolderVerifiedRequests_RedeemsExactlyOnce()
    {
        var (organizerClient, seeded) = await SeedRealNameTicketAsync();

        var responses = await Task.WhenAll(
            RedeemWithHolderFlagAsync(organizerClient, seeded.TicketId, null, isHolderVerified: true),
            RedeemWithHolderFlagAsync(organizerClient, seeded.TicketId, null, isHolderVerified: true));

        responses.Select(r => r.StatusCode).Should().BeEquivalentTo([HttpStatusCode.NoContent, HttpStatusCode.Conflict]);
        var conflict = responses.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        (await RealNameTestData.ReadProblemTitleAsync(conflict)).Should().NotBe(HolderVerificationRequiredTitle);
        await AssertTicketRedeemedAsync(seeded.TicketId);
    }

    // ---- real-name-verification：查詢持票人（RDM-HOLDER-*、RNV-NOSTORE-004／005） ----

    private static readonly string[] HolderFieldNames = ["holderRealName", "holderNationalIdLast4"];

    private static Task<HttpResponseMessage> GetHolderAsync(HttpClient client, Guid ticketId)
        => client.GetAsync($"/api/admin/tickets/{ticketId}/holder");

    private static async Task AssertBodyHasNoHolderDataAsync(HttpResponseMessage response, string realName = "王小明", string nationalIdLast4 = "1234")
    {
        var body = await response.Content.ReadAsStringAsync();
        foreach (var fieldName in HolderFieldNames) body.Should().NotContainEquivalentOf(fieldName);
        HexIdentifierText.RemoveHexIdentifiers(body).Should().NotContain(realName).And.NotContain(nationalIdLast4);
    }

    private static void AssertNoStore(HttpResponseMessage response)
        => response.Headers.CacheControl!.NoStore.Should().BeTrue("持票人查詢的任何回應都不得被瀏覽器或代理快取");

    // [RDM-HOLDER-001]／[RDM-HOLDER-006]／[RNV-NOSTORE-004] 現場比對證件需要完整姓名與末四碼；查詢不得改變票券狀態。
    [Fact]
    public async Task GetHolder_OwnRealNameIssuedTicket_Returns200WithFullHolderDataAndNoStoreAndDoesNotChangeTicket()
    {
        var (organizerClient, seeded) = await SeedRealNameTicketAsync();

        var response = await GetHolderAsync(organizerClient, seeded.TicketId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertNoStore(response);
        var holder = await response.Content.ReadFromJsonAsync<TicketHolderDto>();
        holder.Should().Be(new TicketHolderDto(seeded.TicketId, "Issued", true, "王小明", "1234"));
        await AssertTicketStillIssuedAsync(seeded.TicketId);
    }

    // [RDM-HOLDER-002] 買家剛好有登記也不得在不需實名的活動外洩實名。
    [Fact]
    public async Task GetHolder_OwnNonRealNameTicketWithRegisteredBuyer_Returns200WithNullHolderFields()
    {
        var (organizerClient, seeded) = await SeedRealNameTicketAsync(isRealNameRequired: false);

        var response = await GetHolderAsync(organizerClient, seeded.TicketId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var holder = await response.Content.ReadFromJsonAsync<TicketHolderDto>();
        holder.Should().Be(new TicketHolderDto(seeded.TicketId, "Issued", false, null, null));
    }

    // [RDM-HOLDER-003]／[RDM-HOLDER-010]／[RNV-NOSTORE-005] 其他 Organizer（含以 Admin 角色切換到別的 Organizer）視同不存在。
    [Fact]
    public async Task GetHolder_OtherOrganizerTicket_Returns404WithSameBodyAsMissingTicketForOrganizerAndAdmin()
    {
        var (_, seeded) = await SeedRealNameTicketAsync();
        var (otherOrganizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var adminOfOtherOrganizerClient = await AuthTestHelper.CreateAuthenticatedAdminWithOrganizerContextClientAsync(_factory);
        var missingTicketId = Guid.NewGuid();

        foreach (var client in new[] { otherOrganizerClient, adminOfOtherOrganizerClient })
        {
            var otherOrganizerResponse = await GetHolderAsync(client, seeded.TicketId);
            var missingResponse = await GetHolderAsync(client, missingTicketId);

            otherOrganizerResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
            AssertNoStore(otherOrganizerResponse);
            await AssertBodyHasNoHolderDataAsync(otherOrganizerResponse);
            (await NotFoundResponseBody.ReadNormalizedAsync(otherOrganizerResponse, seeded.TicketId))
                .Should().Be(await NotFoundResponseBody.ReadNormalizedAsync(missingResponse, missingTicketId));
        }
    }

    // [RDM-HOLDER-004] 未切換 Organizer 者（含 Admin 角色）不得讀取實名。
    [Fact]
    public async Task GetHolder_WithoutOrganizerContext_Returns403ForMemberAndAdminWithoutHolderData()
    {
        var (_, seeded) = await SeedRealNameTicketAsync();
        var clients = new (string Identity, HttpClient Client)[]
        {
            ("Member", await OrganizerScopedTestData.CreateAuthenticatedMemberClientAsync(_factory)),
            ("Admin", await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory)),
        };

        foreach (var (identity, client) in clients)
        {
            var response = await GetHolderAsync(client, seeded.TicketId);

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{identity} 未切換 Organizer");
            await AssertBodyHasNoHolderDataAsync(response);
        }
    }

    // [RDM-HOLDER-005]
    [Fact]
    public async Task GetHolder_WithoutAuthorizationHeader_Returns401WithoutHolderData()
    {
        var (_, seeded) = await SeedRealNameTicketAsync();

        var response = await GetHolderAsync(_factory.CreateClient(), seeded.TicketId);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await AssertBodyHasNoHolderDataAsync(response);
    }

    // [RDM-HOLDER-008]／[RNV-NOSTORE-005] 核銷後即不可再讀實名，縮小個資暴露窗口。
    [Fact]
    public async Task GetHolder_OwnRedeemedTicket_Returns409WithoutHolderDataAndNoStore()
    {
        var (organizerClient, seeded) = await SeedRealNameTicketAsync();
        (await RedeemWithHolderFlagAsync(organizerClient, seeded.TicketId, null, isHolderVerified: true)).EnsureSuccessStatusCode();

        var response = await GetHolderAsync(organizerClient, seeded.TicketId);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        AssertNoStore(response);
        await AssertBodyHasNoHolderDataAsync(response);
    }

    // [RDM-HOLDER-009] 活動開演後仍是入場核銷的主要時段，不得以開演時間限制查詢。
    [Fact]
    public async Task GetHolder_WhenEventAlreadyStarted_Returns200WithHolderData()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var seeded = await RealNameTestData.SeedIssuedTicketAsync(_factory, organizerClient, isRealNameRequired: true);
        // 開演後即停售、無法再下單出票（event-sales-window），故先於未來開演時出票，再直接改欄位模擬「出票後已開演」；
        // Event 沒有修改開始時間的 Domain 方法。
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var startedAtUtc = DateTime.UtcNow.AddHours(-1);
            await dbContext.Events.Where(e => e.Id == seeded.EventId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(e => e.StartAtUtc, startedAtUtc));
        }

        var response = await GetHolderAsync(organizerClient, seeded.TicketId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<TicketHolderDto>())!.HolderRealName.Should().Be("王小明");
    }

    [Fact]
    public async Task GetHolder_WithNonGuidId_Returns404()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);

        var response = await organizerClient.GetAsync("/api/admin/tickets/not-a-guid/holder");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
