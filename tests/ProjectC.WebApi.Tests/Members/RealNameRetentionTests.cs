using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProjectC.Application.Tickets.GetTicketHolder;
using ProjectC.Application.Tickets.RedeemTicket;
using ProjectC.Domain.Tickets;
using ProjectC.Infrastructure.Persistence;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Members;

/// <summary>
/// 停用帳號不等於取消已售出的票：持票人仍要能入場，現場比對證件所需的實名因此必須保留（real-name-verification RNV-RETAIN-*）。
/// </summary>
public class RealNameRetentionTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public RealNameRetentionTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<(string? RealName, string? NationalIdLast4)> ReadStoredRealNameAsync(Guid memberId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var member = await dbContext.Members.AsNoTracking().SingleAsync(m => m.Id == memberId);
        return (member.RealName, member.NationalIdLast4);
    }

    private async Task DeactivateAsync(Guid memberId)
    {
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);
        (await adminClient.PostAsync($"/api/admin/members/{memberId}/deactivate", content: null)).EnsureSuccessStatusCode();
    }

    // [RNV-RETAIN-001]
    [Fact]
    public async Task DeactivateThenActivate_KeepsRealNameUnchanged()
    {
        var member = await RealNameTestData.CreateRegisteredMemberAsync(_factory, "保留測試庚", "0246");
        var adminClient = await AuthTestHelper.CreateAuthenticatedAdminClientAsync(_factory);

        (await adminClient.PostAsync($"/api/admin/members/{member.MemberId}/deactivate", content: null)).EnsureSuccessStatusCode();
        (await ReadStoredRealNameAsync(member.MemberId)).Should().Be(("保留測試庚", "0246"));

        (await adminClient.PostAsync($"/api/admin/members/{member.MemberId}/activate", content: null)).EnsureSuccessStatusCode();
        (await ReadStoredRealNameAsync(member.MemberId)).Should().Be(("保留測試庚", "0246"));
    }

    // [RNV-RETAIN-002]
    [Fact]
    public async Task GetHolder_AfterBuyerDeactivated_Returns200WithBuyerRealName()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var seeded = await RealNameTestData.SeedIssuedTicketAsync(_factory, organizerClient, isRealNameRequired: true, "保留測試辛", "1357");
        await DeactivateAsync(seeded.Buyer.MemberId);

        var response = await organizerClient.GetAsync($"/api/admin/tickets/{seeded.TicketId}/holder");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<TicketHolderDto>())
            .Should().Be(new TicketHolderDto(seeded.TicketId, "Issued", true, "保留測試辛", "1357"));
    }

    // [RNV-RETAIN-003]
    [Fact]
    public async Task Redeem_AfterBuyerDeactivatedWithHolderVerified_Returns204AndRedeemsTicket()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var seeded = await RealNameTestData.SeedIssuedTicketAsync(_factory, organizerClient, isRealNameRequired: true);
        await DeactivateAsync(seeded.Buyer.MemberId);

        var response = await organizerClient.PatchAsJsonAsync(
            $"/api/admin/tickets/{seeded.TicketId}/redeem", new RedeemTicketRequest(null, IsHolderVerified: true));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await OrganizerScopedTestData.ReadTicketAsync(_factory, seeded.TicketId)).Status.Should().Be(TicketStatus.Redeemed);
    }
}
