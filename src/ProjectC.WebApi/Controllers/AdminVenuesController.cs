using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProjectC.Application.Venues.CreateSeatMap;
using ProjectC.Application.Venues.CreateVenue;
using ProjectC.WebApi.Common;

namespace ProjectC.WebApi.Controllers;

[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
[ApiController]
[Route("api/admin/venues")]
public class AdminVenuesController : ControllerBase
{
    private readonly CreateVenueHandler _createVenueHandler;
    private readonly CreateSeatMapHandler _createSeatMapHandler;

    public AdminVenuesController(CreateVenueHandler createVenueHandler, CreateSeatMapHandler createSeatMapHandler)
    {
        _createVenueHandler = createVenueHandler;
        _createSeatMapHandler = createSeatMapHandler;
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
}
