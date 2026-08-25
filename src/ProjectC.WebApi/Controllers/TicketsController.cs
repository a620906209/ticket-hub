using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProjectC.Application.Tickets.GetTicketQrCode;
using ProjectC.WebApi.Common;

namespace ProjectC.WebApi.Controllers;

[Authorize]
[ApiController]
[Route("api/tickets")]
public class TicketsController : ControllerBase
{
    private readonly GetTicketQrCodeHandler _getTicketQrCodeHandler;

    public TicketsController(GetTicketQrCodeHandler getTicketQrCodeHandler)
    {
        _getTicketQrCodeHandler = getTicketQrCodeHandler;
    }

    [HttpGet("{id:guid}/qr-code")]
    public async Task<IActionResult> GetQrCode(Guid id, CancellationToken cancellationToken)
    {
        var result = await _getTicketQrCodeHandler.HandleAsync(id, User.GetMemberId(), cancellationToken);
        return result.ToActionResult(bytes => File(bytes, "image/png"));
    }
}
