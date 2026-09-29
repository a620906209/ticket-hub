using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProjectC.Application.Organizers.GetPendingOrganizers;
using ProjectC.Application.Organizers.ReviewOrganizer;
using ProjectC.Application.Organizers.SuspendOrganizer;
using ProjectC.WebApi.Common;

namespace ProjectC.WebApi.Controllers;

[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
[ApiController]
[Route("api/admin/organizers")]
public class AdminOrganizersController : ControllerBase
{
    private readonly GetPendingOrganizersHandler _getPendingOrganizersHandler;
    private readonly ReviewOrganizerHandler _reviewOrganizerHandler;
    private readonly SuspendOrganizerHandler _suspendOrganizerHandler;

    public AdminOrganizersController(
        GetPendingOrganizersHandler getPendingOrganizersHandler,
        ReviewOrganizerHandler reviewOrganizerHandler,
        SuspendOrganizerHandler suspendOrganizerHandler)
    {
        _getPendingOrganizersHandler = getPendingOrganizersHandler;
        _reviewOrganizerHandler = reviewOrganizerHandler;
        _suspendOrganizerHandler = suspendOrganizerHandler;
    }

    [HttpGet]
    public async Task<IActionResult> GetPending([FromQuery] string? status, CancellationToken cancellationToken)
    {
        // 本次唯一支援的查詢目標即為 Pending（見 organizer-management spec.md「平台管理員可以審核
        // Organizer 申請」需求），status 參數僅為呼叫端可讀性保留，不做其他值的分支處理（YAGNI）。
        var organizers = await _getPendingOrganizersHandler.HandleAsync(cancellationToken);
        return Ok(organizers);
    }

    [HttpPatch("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, CancellationToken cancellationToken)
    {
        var result = await _reviewOrganizerHandler.ApproveAsync(id, User.GetMemberId(), cancellationToken);
        return result.ToActionResult();
    }

    [HttpPatch("{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, CancellationToken cancellationToken)
    {
        var result = await _reviewOrganizerHandler.RejectAsync(id, User.GetMemberId(), cancellationToken);
        return result.ToActionResult();
    }

    [HttpPatch("{id:guid}/suspend")]
    public async Task<IActionResult> Suspend(Guid id, CancellationToken cancellationToken)
    {
        var result = await _suspendOrganizerHandler.HandleAsync(id, cancellationToken);
        return result.ToActionResult();
    }
}
