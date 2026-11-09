using Microsoft.AspNetCore.Mvc;
using ProjectC.Application.Common.Paging;
using ProjectC.Application.Events.GetEventById;
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
    private readonly GetEventByIdHandler _getEventByIdHandler;
    private readonly GetEventSeatsHandler _getEventSeatsHandler;
    private readonly GetTicketTypesHandler _getTicketTypesHandler;

    public EventsController(
        GetEventsHandler getEventsHandler,
        GetEventByIdHandler getEventByIdHandler,
        GetEventSeatsHandler getEventSeatsHandler,
        GetTicketTypesHandler getTicketTypesHandler)
    {
        _getEventsHandler = getEventsHandler;
        _getEventByIdHandler = getEventByIdHandler;
        _getEventSeatsHandler = getEventSeatsHandler;
        _getTicketTypesHandler = getTicketTypesHandler;
    }

    [HttpGet]
    public async Task<IActionResult> GetEvents(
        CancellationToken cancellationToken,
        [FromQuery] int page = PageRequest.DefaultPage,
        [FromQuery] int pageSize = PageRequest.DefaultPageSize)
    {
        var result = await _getEventsHandler.HandleAsync(new PageRequest(page, pageSize), cancellationToken);
        return result.ToActionResult(Ok);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetEventById(Guid id, CancellationToken cancellationToken)
    {
        var result = await _getEventByIdHandler.HandleAsync(id, cancellationToken);
        return result.ToActionResult(Ok);
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
