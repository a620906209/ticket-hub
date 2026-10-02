using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using ProjectC.WebApi.Tests.TestSupport;

namespace ProjectC.WebApi.Tests.Admin;

/// <summary>
/// [RDM-HOLDER-007] 下單閘門保證需實名活動的買家必已登記，實名又只增不減；查不到代表資料損毀，必須 500 大聲失敗，
/// 不能回 200 + null 讓現場人員誤以為「這張票不需比對證件」。真實資料無法造出這種狀態，改以 decorator 注入。
/// </summary>
public class TicketHolderDataCorruptionTests : IClassFixture<RealNameFaultInjectionWebApplicationFactory>
{
    private const string HolderRealName = "資料損毀測試乙";
    private const string HolderNationalIdLast4 = "9753";

    private readonly RealNameFaultInjectionWebApplicationFactory _factory;

    public TicketHolderDataCorruptionTests(RealNameFaultInjectionWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetHolder_WhenRealNameRequiredButBuyerRealNameMissing_Returns500WithoutHolderDataAndLogsOnlyIds()
    {
        var (organizerClient, _) = await AuthTestHelper.CreateAuthenticatedApprovedOrganizerClientAsync(_factory);
        var seeded = await RealNameTestData.SeedIssuedTicketAsync(
            _factory, organizerClient, isRealNameRequired: true, HolderRealName, HolderNationalIdLast4);
        _factory.MissingRealNameMemberIds.TryAdd(seeded.Buyer.MemberId, 0);

        var response = await organizerClient.GetAsync($"/api/admin/tickets/{seeded.TicketId}/holder");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var body = await response.Content.ReadAsStringAsync();
        HexIdentifierText.RemoveHexIdentifiers(body).Should().NotContainEquivalentOf("holderRealName").And.NotContainEquivalentOf("holderNationalIdLast4")
            .And.NotContain(HolderRealName).And.NotContain(HolderNationalIdLast4);

        var exceptionMessage = _factory.ExceptionHandlerLogger.Entries
            .Single(e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException
                && e.Exception.Message.Contains(seeded.TicketId.ToString()))
            .Exception!.Message;
        var guidsInMessage = Regex.Matches(exceptionMessage, "[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}")
            .Select(m => Guid.Parse(m.Value));
        guidsInMessage.Should().BeEquivalentTo([seeded.TicketId, seeded.Buyer.MemberId]);
        HexIdentifierText.RemoveHexIdentifiers(exceptionMessage).Should().NotContain(HolderRealName).And.NotContain(HolderNationalIdLast4);
    }
}
