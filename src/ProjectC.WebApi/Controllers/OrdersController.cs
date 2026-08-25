using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProjectC.Application.Orders;
using ProjectC.Application.Orders.GetMyOrderDetail;
using ProjectC.Application.Orders.GetMyOrders;
using ProjectC.Application.Orders.PlaceOrder;
using ProjectC.WebApi.Common;

namespace ProjectC.WebApi.Controllers;

[Authorize]
[ApiController]
[Route("api/orders")]
public class OrdersController : ControllerBase
{
    private readonly OrderService _orderService;
    private readonly GetMyOrdersHandler _getMyOrdersHandler;
    private readonly GetMyOrderDetailHandler _getMyOrderDetailHandler;

    public OrdersController(
        OrderService orderService,
        GetMyOrdersHandler getMyOrdersHandler,
        GetMyOrderDetailHandler getMyOrderDetailHandler)
    {
        _orderService = orderService;
        _getMyOrdersHandler = getMyOrdersHandler;
        _getMyOrderDetailHandler = getMyOrderDetailHandler;
    }

    [HttpGet]
    public async Task<IActionResult> GetMyOrders(CancellationToken cancellationToken)
    {
        var orders = await _getMyOrdersHandler.HandleAsync(User.GetMemberId(), cancellationToken);
        return Ok(orders);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetMyOrderDetail(Guid id, CancellationToken cancellationToken)
    {
        var result = await _getMyOrderDetailHandler.HandleAsync(id, User.GetMemberId(), cancellationToken);
        return result.ToActionResult(Ok);
    }

    [HttpPost]
    public async Task<IActionResult> PlaceOrder(PlaceOrderRequest request, CancellationToken cancellationToken)
    {
        var result = await _orderService.PlaceOrderAsync(User.GetMemberId(), request, cancellationToken);
        return result.ToActionResult(id => StatusCode(StatusCodes.Status201Created, new { id }));
    }

    [HttpPost("{id:guid}/confirm")]
    public async Task<IActionResult> ConfirmOrder(Guid id, CancellationToken cancellationToken)
    {
        var result = await _orderService.ConfirmOrderAsync(id, User.GetMemberId(), cancellationToken);
        return result.ToActionResult();
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> CancelOrder(Guid id, CancellationToken cancellationToken)
    {
        var result = await _orderService.CancelOrderAsync(id, User.GetMemberId(), cancellationToken);
        return result.ToActionResult();
    }
}
