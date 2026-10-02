using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProjectC.Application.Members.GetMyProfile;
using ProjectC.Application.Members.RegisterRealName;
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
    private readonly RegisterRealNameHandler _registerRealNameHandler;

    public MembersController(
        GetMyProfileHandler getMyProfileHandler,
        UpdateMyProfileHandler updateMyProfileHandler,
        RegisterRealNameHandler registerRealNameHandler)
    {
        _getMyProfileHandler = getMyProfileHandler;
        _updateMyProfileHandler = updateMyProfileHandler;
        _registerRealNameHandler = registerRealNameHandler;
    }

    // 回應含實名；NoStore 是 result filter，涵蓋此 action 產生的所有狀態碼（real-name-verification design.md 決策 5「回應快取」）。
    [HttpGet("me")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
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

    [HttpPut("me/real-name")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> RegisterRealName(RegisterRealNameRequest request, CancellationToken cancellationToken)
    {
        var result = await _registerRealNameHandler.HandleAsync(User.GetMemberId(), request, cancellationToken);
        return result.ToActionResult(Ok);
    }
}
