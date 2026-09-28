using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProjectC.Application.Tickets.RedeemTicket;
using ProjectC.WebApi.Common;

namespace ProjectC.WebApi.Controllers;

[Authorize(Policy = AuthorizationPolicies.RequireOrganizerContext)]
[ApiController]
[Route("api/admin/tickets")]
public class AdminTicketsController : ControllerBase
{
    private readonly RedeemTicketHandler _redeemTicketHandler;

    public AdminTicketsController(RedeemTicketHandler redeemTicketHandler)
    {
        _redeemTicketHandler = redeemTicketHandler;
    }

    [HttpPatch("{id:guid}/redeem")]
    public async Task<IActionResult> Redeem(Guid id, [FromBody] RedeemTicketRequest? request, CancellationToken cancellationToken)
    {
        if (!User.TryGetOrganizerId(out var organizerId))
        {
            // RequireOrganizerContext Policy 已保證走到這裡必定有合法 claim；此分支是防日後漏掛 Policy 的第二道防線，fail-closed。
            return Forbid();
        }

        var result = await _redeemTicketHandler.HandleAsync(id, organizerId, request?.Signature, cancellationToken);
        return result.ToActionResult();
    }
}
