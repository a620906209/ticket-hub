using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using ProjectC.Domain.Tickets;
using ProjectC.WebApi.Tests.TestSupport;

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
}
