using Microsoft.AspNetCore.Mvc;
using ProjectC.Application.Events.GetEventSeats;
using ProjectC.Application.Events.GetEvents;
using ProjectC.Application.Tickets.GetTicketTypes;
using ProjectC.WebApi.Common;

namespace ProjectC.WebApi.Controllers;

[ApiController]
[Route("api/events")]
public class EventsController : ControllerBase
{
    private readonly GetEventsHandler _getEventsHandler;
    private readonly GetEventSeatsHandler _getEventSeatsHandler;
    private readonly GetTicketTypesHandler _getTicketTypesHandler;

    public EventsController(
        GetEventsHandler getEventsHandler,
        GetEventSeatsHandler getEventSeatsHandler,
        GetTicketTypesHandler getTicketTypesHandler)
    {
        _getEventsHandler = getEventsHandler;
        _getEventSeatsHandler = getEventSeatsHandler;
        _getTicketTypesHandler = getTicketTypesHandler;
    }

    [HttpGet]
    public async Task<IActionResult> GetEvents(CancellationToken cancellationToken)
    {
        var events = await _getEventsHandler.HandleAsync(cancellationToken);
        return Ok(events);
    }

    [HttpGet("{id:guid}/seats")]
    public async Task<IActionResult> GetEventSeats(Guid id, CancellationToken cancellationToken)
    {
        var result = await _getEventSeatsHandler.HandleAsync(id, cancellationToken);
        return result.ToActionResult(Ok);
    }

    [HttpGet("{id:guid}/ticket-types")]
    public async Task<IActionResult> GetTicketTypes(Guid id, CancellationToken cancellationToken)
    {
        var result = await _getTicketTypesHandler.HandleAsync(id, cancellationToken);
        return result.ToActionResult(Ok);
    }
}
