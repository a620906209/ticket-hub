using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProjectC.Application.Tickets.GetTicketHolder;
using ProjectC.Application.Tickets.RedeemTicket;
using ProjectC.WebApi.Common;

namespace ProjectC.WebApi.Controllers;

[Authorize(Policy = AuthorizationPolicies.RequireOrganizerContext)]
[ApiController]
[Route("api/admin/tickets")]
public class AdminTicketsController : ControllerBase
{
    private readonly RedeemTicketHandler _redeemTicketHandler;
    private readonly GetTicketHolderHandler _getTicketHolderHandler;

    public AdminTicketsController(RedeemTicketHandler redeemTicketHandler, GetTicketHolderHandler getTicketHolderHandler)
    {
        _redeemTicketHandler = redeemTicketHandler;
        _getTicketHolderHandler = getTicketHolderHandler;
    }

    [HttpPatch("{id:guid}/redeem")]
    public async Task<IActionResult> Redeem(Guid id, [FromBody] RedeemTicketRequest? request, CancellationToken cancellationToken)
    {
        if (!User.TryGetOrganizerId(out var organizerId))
        {
            // RequireOrganizerContext Policy 已保證走到這裡必定有合法 claim；此分支是防日後漏掛 Policy 的第二道防線，fail-closed。
            return Forbid();
        }

        var result = await _redeemTicketHandler.HandleAsync(id, organizerId, request, cancellationToken);
        return result.ToActionResult();
    }

    // 回應含完整實名與末四碼，核銷頁可能是多人共用的現場裝置；NoStore 涵蓋此 action 產生的 200／404／409
    // （real-name-verification design.md 決策 5「回應快取」）。
    [HttpGet("{id:guid}/holder")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> GetHolder(Guid id, CancellationToken cancellationToken)
    {
        if (!User.TryGetOrganizerId(out var organizerId))
        {
            return Forbid();
        }

        var result = await _getTicketHolderHandler.HandleAsync(id, organizerId, User.GetMemberId(), cancellationToken);
        return result.ToActionResult(Ok);
    }
}
