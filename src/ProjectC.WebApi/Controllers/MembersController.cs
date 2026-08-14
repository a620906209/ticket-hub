using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProjectC.Application.Members.GetMyProfile;
using ProjectC.Application.Members.UpdateMyProfile;
using ProjectC.WebApi.Common;

namespace ProjectC.WebApi.Controllers;

[Authorize]
[ApiController]
[Route("api/members")]
public class MembersController : ControllerBase
{
    private readonly GetMyProfileHandler _getMyProfileHandler;
    private readonly UpdateMyProfileHandler _updateMyProfileHandler;

    public MembersController(GetMyProfileHandler getMyProfileHandler, UpdateMyProfileHandler updateMyProfileHandler)
    {
        _getMyProfileHandler = getMyProfileHandler;
        _updateMyProfileHandler = updateMyProfileHandler;
    }

    [HttpGet("me")]
    public async Task<IActionResult> GetMe(CancellationToken cancellationToken)
    {
        var result = await _getMyProfileHandler.HandleAsync(User.GetMemberId(), cancellationToken);
        return result.ToActionResult(Ok);
    }

    [HttpPut("me")]
    public async Task<IActionResult> UpdateMe(UpdateMyProfileRequest request, CancellationToken cancellationToken)
    {
        var result = await _updateMyProfileHandler.HandleAsync(User.GetMemberId(), request, cancellationToken);
        return result.ToActionResult(Ok);
    }
}
