using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProjectC.Application.Events.CreateEvent;
using ProjectC.Application.Events.GetAdminEvents;
using ProjectC.Application.Events.SetEventQueueMode;
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
    private readonly GetAdminEventsHandler _getAdminEventsHandler;
    private readonly SetEventQueueModeHandler _setEventQueueModeHandler;

    public AdminEventsController(
        CreateEventHandler createEventHandler,
        CreateTicketTypeHandler createTicketTypeHandler,
        GetAdminEventsHandler getAdminEventsHandler,
        SetEventQueueModeHandler setEventQueueModeHandler)
    {
        _createEventHandler = createEventHandler;
        _createTicketTypeHandler = createTicketTypeHandler;
        _getAdminEventsHandler = getAdminEventsHandler;
        _setEventQueueModeHandler = setEventQueueModeHandler;
    }

    [HttpPost]
    public async Task<IActionResult> CreateEvent(CreateEventRequest request, CancellationToken cancellationToken)
    {
        var result = await _createEventHandler.HandleAsync(User.GetMemberId(), request, cancellationToken);
        return result.ToActionResult(id => StatusCode(StatusCodes.Status201Created, new { id }));
    }

    [HttpGet]
    public async Task<IActionResult> GetEvents(CancellationToken cancellationToken)
    {
        var events = await _getAdminEventsHandler.HandleAsync(cancellationToken);
        return Ok(events);
    }

    [HttpPost("{eventId:guid}/ticket-types")]
    public async Task<IActionResult> CreateTicketType(Guid eventId, CreateTicketTypeRequest request, CancellationToken cancellationToken)
    {
        var result = await _createTicketTypeHandler.HandleAsync(eventId, request, cancellationToken);
        return result.ToActionResult(id => StatusCode(StatusCodes.Status201Created, new { id }));
    }

    [HttpPatch("{id:guid}/queue-mode")]
    public async Task<IActionResult> SetQueueMode(Guid id, SetEventQueueModeRequest request, CancellationToken cancellationToken)
    {
        var result = await _setEventQueueModeHandler.HandleAsync(id, request, cancellationToken);
        return result.ToActionResult();
    }
}
