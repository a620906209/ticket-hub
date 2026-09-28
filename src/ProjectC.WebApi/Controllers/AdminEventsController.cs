using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProjectC.Application.Events.CreateEvent;
using ProjectC.Application.Events.GetAdminEvents;
using ProjectC.Application.Events.SetEventQueueMode;
using ProjectC.Application.Orders.GetEventSalesReport;
using ProjectC.Application.Tickets.CreateTicketType;
using ProjectC.WebApi.Common;

namespace ProjectC.WebApi.Controllers;

[ApiController]
[Route("api/admin/events")]
public class AdminEventsController : ControllerBase
{
    private readonly CreateEventHandler _createEventHandler;
    private readonly CreateTicketTypeHandler _createTicketTypeHandler;
    private readonly GetAdminEventsHandler _getAdminEventsHandler;
    private readonly SetEventQueueModeHandler _setEventQueueModeHandler;
    private readonly GetEventSalesReportHandler _getEventSalesReportHandler;

    public AdminEventsController(
        CreateEventHandler createEventHandler,
        CreateTicketTypeHandler createTicketTypeHandler,
        GetAdminEventsHandler getAdminEventsHandler,
        SetEventQueueModeHandler setEventQueueModeHandler,
        GetEventSalesReportHandler getEventSalesReportHandler)
    {
        _createEventHandler = createEventHandler;
        _createTicketTypeHandler = createTicketTypeHandler;
        _getAdminEventsHandler = getAdminEventsHandler;
        _setEventQueueModeHandler = setEventQueueModeHandler;
        _getEventSalesReportHandler = getEventSalesReportHandler;
    }

    [Authorize(Policy = AuthorizationPolicies.RequireOrganizerContext)]
    [HttpPost]
    public async Task<IActionResult> CreateEvent(CreateEventRequest request, CancellationToken cancellationToken)
    {
        if (!User.TryGetOrganizerId(out var organizerId))
        {
            // RequireOrganizerContext Policy 已保證走到這裡必定有合法 claim；此分支只是防日後漏掛 Policy 時
            // 以 Guid.Empty 往下呼叫（CreateEvent 會在 Domain guard 變成 500、GetEvents 會靜默回空清單），改為 fail-closed。
            return Forbid();
        }

        var result = await _createEventHandler.HandleAsync(User.GetMemberId(), organizerId, request, cancellationToken);
        return result.ToActionResult(id => StatusCode(StatusCodes.Status201Created, new { id }));
    }

    [Authorize(Policy = AuthorizationPolicies.RequireOrganizerContext)]
    [HttpGet]
    public async Task<IActionResult> GetEvents(CancellationToken cancellationToken)
    {
        if (!User.TryGetOrganizerId(out var organizerId))
        {
            // fail-closed，理由同 CreateEvent。
            return Forbid();
        }

        var events = await _getAdminEventsHandler.HandleAsync(organizerId, cancellationToken);
        return Ok(events);
    }

    [Authorize(Policy = AuthorizationPolicies.RequireOrganizerContext)]
    [HttpPost("{eventId:guid}/ticket-types")]
    public async Task<IActionResult> CreateTicketType(Guid eventId, CreateTicketTypeRequest request, CancellationToken cancellationToken)
    {
        if (!User.TryGetOrganizerId(out var organizerId))
        {
            // fail-closed，理由同 CreateEvent。
            return Forbid();
        }

        var result = await _createTicketTypeHandler.HandleAsync(eventId, organizerId, request, cancellationToken);
        return result.ToActionResult(id => StatusCode(StatusCodes.Status201Created, new { id }));
    }

    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [HttpPatch("{id:guid}/queue-mode")]
    public async Task<IActionResult> SetQueueMode(Guid id, SetEventQueueModeRequest request, CancellationToken cancellationToken)
    {
        var result = await _setEventQueueModeHandler.HandleAsync(id, request, cancellationToken);
        return result.ToActionResult();
    }

    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [HttpGet("{eventId:guid}/sales-report")]
    public async Task<IActionResult> GetSalesReport(Guid eventId, CancellationToken cancellationToken)
    {
        var result = await _getEventSalesReportHandler.HandleAsync(eventId, cancellationToken);
        return result.ToActionResult(Ok);
    }
}
