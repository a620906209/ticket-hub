using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProjectC.Application.Tickets.RedeemTicket;
using ProjectC.WebApi.Common;

namespace ProjectC.WebApi.Controllers;

[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
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
        var result = await _redeemTicketHandler.HandleAsync(id, request?.Signature, cancellationToken);
        return result.ToActionResult();
    }
}
