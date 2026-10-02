using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
}
