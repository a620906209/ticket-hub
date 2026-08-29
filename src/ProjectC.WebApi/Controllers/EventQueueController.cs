using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProjectC.Application.PurchaseQueue.GetMyQueueStatus;
using ProjectC.Application.PurchaseQueue.JoinPurchaseQueue;
using ProjectC.WebApi.Common;

namespace ProjectC.WebApi.Controllers;

// 只要求已登入、不限角色，比照既有 POST /api/orders 慣例（rate-limiting-queue design.md 決策 7）。
[Authorize]
[ApiController]
[Route("api/events/{id:guid}/queue")]
public class EventQueueController : ControllerBase
{
    private readonly JoinPurchaseQueueHandler _joinPurchaseQueueHandler;
    private readonly GetMyQueueStatusHandler _getMyQueueStatusHandler;

    public EventQueueController(
        JoinPurchaseQueueHandler joinPurchaseQueueHandler,
        GetMyQueueStatusHandler getMyQueueStatusHandler)
    {
        _joinPurchaseQueueHandler = joinPurchaseQueueHandler;
        _getMyQueueStatusHandler = getMyQueueStatusHandler;
    }

    [HttpPost("entries")]
    public async Task<IActionResult> JoinQueue(Guid id, CancellationToken cancellationToken)
    {
        var result = await _joinPurchaseQueueHandler.HandleAsync(id, User.GetMemberId(), cancellationToken);
        return result.ToActionResult(entryId => StatusCode(StatusCodes.Status201Created, new { id = entryId }));
    }

    [HttpGet("entries/me")]
    public async Task<IActionResult> GetMyQueueStatus(Guid id, CancellationToken cancellationToken)
    {
        var result = await _getMyQueueStatusHandler.HandleAsync(id, User.GetMemberId(), cancellationToken);
        return result.ToActionResult(Ok);
    }
}
