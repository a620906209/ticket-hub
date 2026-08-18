using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProjectC.Application.Venues.CreateSeatMap;
using ProjectC.Application.Venues.CreateVenue;
using ProjectC.Application.Venues.GetSeatMapById;
using ProjectC.Application.Venues.GetVenueById;
using ProjectC.Application.Venues.GetVenues;
using ProjectC.WebApi.Common;

namespace ProjectC.WebApi.Controllers;

[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
[ApiController]
[Route("api/admin/venues")]
public class AdminVenuesController : ControllerBase
{
    private readonly CreateVenueHandler _createVenueHandler;
    private readonly CreateSeatMapHandler _createSeatMapHandler;
    private readonly GetVenuesHandler _getVenuesHandler;
    private readonly GetVenueByIdHandler _getVenueByIdHandler;
    private readonly GetSeatMapByIdHandler _getSeatMapByIdHandler;

    public AdminVenuesController(
        CreateVenueHandler createVenueHandler,
        CreateSeatMapHandler createSeatMapHandler,
        GetVenuesHandler getVenuesHandler,
        GetVenueByIdHandler getVenueByIdHandler,
        GetSeatMapByIdHandler getSeatMapByIdHandler)
    {
        _createVenueHandler = createVenueHandler;
        _createSeatMapHandler = createSeatMapHandler;
        _getVenuesHandler = getVenuesHandler;
        _getVenueByIdHandler = getVenueByIdHandler;
        _getSeatMapByIdHandler = getSeatMapByIdHandler;
    }

    [HttpPost]
    public async Task<IActionResult> CreateVenue(CreateVenueRequest request, CancellationToken cancellationToken)
    {
        var result = await _createVenueHandler.HandleAsync(request, cancellationToken);
        return result.ToActionResult(id => StatusCode(StatusCodes.Status201Created, new { id }));
    }

    [HttpPost("{venueId:guid}/seat-maps")]
    public async Task<IActionResult> CreateSeatMap(Guid venueId, CreateSeatMapRequest request, CancellationToken cancellationToken)
    {
        var result = await _createSeatMapHandler.HandleAsync(venueId, request, cancellationToken);
        return result.ToActionResult(id => StatusCode(StatusCodes.Status201Created, new { id }));
    }

    [HttpGet]
    public async Task<IActionResult> GetVenues(CancellationToken cancellationToken)
    {
        var venues = await _getVenuesHandler.HandleAsync(cancellationToken);
        return Ok(venues);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetVenueById(Guid id, CancellationToken cancellationToken)
    {
        var result = await _getVenueByIdHandler.HandleAsync(id, cancellationToken);
        return result.ToActionResult(Ok);
    }

    [HttpGet("{venueId:guid}/seat-maps/{seatMapId:guid}")]
    public async Task<IActionResult> GetSeatMapById(Guid venueId, Guid seatMapId, CancellationToken cancellationToken)
    {
        var result = await _getSeatMapByIdHandler.HandleAsync(venueId, seatMapId, cancellationToken);
        return result.ToActionResult(Ok);
    }
}
