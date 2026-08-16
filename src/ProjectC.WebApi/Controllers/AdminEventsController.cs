using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProjectC.Application.Events.CreateEvent;
using ProjectC.Application.Tickets.CreateTicketType;
using ProjectC.WebApi.Common;

namespace ProjectC.WebApi.Controllers;

[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
[ApiController]
[Route("api/admin/events")]
public class AdminEventsController : ControllerBase
{
    private readonly CreateEventHandler _createEventHandler;
    private readonly CreateTicketTypeHandler _createTicketTypeHandler;

    public AdminEventsController(CreateEventHandler createEventHandler, CreateTicketTypeHandler createTicketTypeHandler)
    {
        _createEventHandler = createEventHandler;
        _createTicketTypeHandler = createTicketTypeHandler;
    }

    [HttpPost]
    public async Task<IActionResult> CreateEvent(CreateEventRequest request, CancellationToken cancellationToken)
    {
        var result = await _createEventHandler.HandleAsync(request, cancellationToken);
        return result.ToActionResult(id => StatusCode(StatusCodes.Status201Created, new { id }));
    }

    [HttpPost("{eventId:guid}/ticket-types")]
    public async Task<IActionResult> CreateTicketType(Guid eventId, CreateTicketTypeRequest request, CancellationToken cancellationToken)
    {
        var result = await _createTicketTypeHandler.HandleAsync(eventId, request, cancellationToken);
        return result.ToActionResult(id => StatusCode(StatusCodes.Status201Created, new { id }));
    }
}
