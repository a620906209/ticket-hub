using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProjectC.Application.Orders.GetOrderById;
using ProjectC.Application.Orders.GetOrders;
using ProjectC.WebApi.Common;

namespace ProjectC.WebApi.Controllers;

[Authorize(Policy = AuthorizationPolicies.RequireOrganizerContext)]
[ApiController]
[Route("api/admin/orders")]
public class AdminOrdersController : ControllerBase
{
    private readonly GetOrdersHandler _getOrdersHandler;
    private readonly GetOrderByIdHandler _getOrderByIdHandler;

    public AdminOrdersController(GetOrdersHandler getOrdersHandler, GetOrderByIdHandler getOrderByIdHandler)
    {
        _getOrdersHandler = getOrdersHandler;
        _getOrderByIdHandler = getOrderByIdHandler;
    }

    [HttpGet]
    public async Task<IActionResult> GetOrders(CancellationToken cancellationToken)
    {
        if (!User.TryGetOrganizerId(out var organizerId))
        {
            // RequireOrganizerContext Policy 已保證走到這裡必定有合法 claim；此分支是防日後漏掛 Policy 的第二道防線，fail-closed。
            return Forbid();
        }

        var orders = await _getOrdersHandler.HandleAsync(organizerId, cancellationToken);
        return Ok(orders);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetOrderById(Guid id, CancellationToken cancellationToken)
    {
        if (!User.TryGetOrganizerId(out var organizerId))
        {
            // RequireOrganizerContext Policy 已保證走到這裡必定有合法 claim；此分支是防日後漏掛 Policy 的第二道防線，fail-closed。
            return Forbid();
        }

        var result = await _getOrderByIdHandler.HandleAsync(id, organizerId, cancellationToken);
        return result.ToActionResult(Ok);
    }
}
