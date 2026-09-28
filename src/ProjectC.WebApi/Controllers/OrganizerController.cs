using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProjectC.Application.Organizers.ApplyForOrganizer;
using ProjectC.Application.Organizers.GetMyOrganizers;
using ProjectC.Application.Organizers.SwitchOrganizerContext;
using ProjectC.WebApi.Common;

namespace ProjectC.WebApi.Controllers;

[Authorize]
[ApiController]
[Route("api/organizers")]
public class OrganizerController : ControllerBase
{
    private readonly ApplyForOrganizerHandler _applyForOrganizerHandler;
    private readonly GetMyOrganizersHandler _getMyOrganizersHandler;
    private readonly SwitchOrganizerContextHandler _switchOrganizerContextHandler;

    public OrganizerController(
        ApplyForOrganizerHandler applyForOrganizerHandler,
        GetMyOrganizersHandler getMyOrganizersHandler,
        SwitchOrganizerContextHandler switchOrganizerContextHandler)
    {
        _applyForOrganizerHandler = applyForOrganizerHandler;
        _getMyOrganizersHandler = getMyOrganizersHandler;
        _switchOrganizerContextHandler = switchOrganizerContextHandler;
    }

    [HttpPost]
    public async Task<IActionResult> Apply(ApplyForOrganizerRequest request, CancellationToken cancellationToken)
    {
        var result = await _applyForOrganizerHandler.HandleAsync(User.GetMemberId(), request, cancellationToken);
        return result.ToActionResult(id => StatusCode(StatusCodes.Status201Created, new { id }));
    }

    [HttpGet("mine")]
    public async Task<IActionResult> GetMine(CancellationToken cancellationToken)
    {
        var organizers = await _getMyOrganizersHandler.HandleAsync(User.GetMemberId(), cancellationToken);
        return Ok(organizers);
    }

    [HttpPost("{id:guid}/switch-context")]
    public async Task<IActionResult> SwitchContext(Guid id, SwitchOrganizerContextRequest request, CancellationToken cancellationToken)
    {
        var result = await _switchOrganizerContextHandler.HandleAsync(User.GetMemberId(), id, request, cancellationToken);
        return result.ToActionResult(Ok);
    }
}
