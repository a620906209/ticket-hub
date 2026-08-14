using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProjectC.Application.Members.Activate;
using ProjectC.Application.Members.Deactivate;
using ProjectC.WebApi.Common;

namespace ProjectC.WebApi.Controllers;

[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
[ApiController]
[Route("api/admin/members")]
public class AdminMembersController : ControllerBase
{
    private readonly ActivateMemberHandler _activateMemberHandler;
    private readonly DeactivateMemberHandler _deactivateMemberHandler;

    public AdminMembersController(ActivateMemberHandler activateMemberHandler, DeactivateMemberHandler deactivateMemberHandler)
    {
        _activateMemberHandler = activateMemberHandler;
        _deactivateMemberHandler = deactivateMemberHandler;
    }

    [HttpPost("{id:guid}/activate")]
    public async Task<IActionResult> Activate(Guid id, CancellationToken cancellationToken)
    {
        var result = await _activateMemberHandler.HandleAsync(id, cancellationToken);
        return result.ToActionResult();
    }

    [HttpPost("{id:guid}/deactivate")]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken cancellationToken)
    {
        var result = await _deactivateMemberHandler.HandleAsync(id, cancellationToken);
        return result.ToActionResult();
    }
}
