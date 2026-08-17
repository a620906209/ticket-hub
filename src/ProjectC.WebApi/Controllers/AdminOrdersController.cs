using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProjectC.Application.Orders.GetOrderById;
using ProjectC.Application.Orders.GetOrders;
using ProjectC.WebApi.Common;

namespace ProjectC.WebApi.Controllers;

[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
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
        var orders = await _getOrdersHandler.HandleAsync(cancellationToken);
        return Ok(orders);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetOrderById(Guid id, CancellationToken cancellationToken)
    {
        var result = await _getOrderByIdHandler.HandleAsync(id, cancellationToken);
        return result.ToActionResult(Ok);
    }
}
