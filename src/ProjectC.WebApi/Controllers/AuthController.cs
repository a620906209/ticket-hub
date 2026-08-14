using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProjectC.Application.Authentication.Login;
using ProjectC.Application.Authentication.Logout;
using ProjectC.Application.Authentication.PasswordReset;
using ProjectC.Application.Authentication.Refresh;
using ProjectC.Application.Members.Register;
using ProjectC.WebApi.Common;

namespace ProjectC.WebApi.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly RegisterMemberHandler _registerMemberHandler;
    private readonly LoginHandler _loginHandler;
    private readonly RefreshTokenHandler _refreshTokenHandler;
    private readonly LogoutHandler _logoutHandler;
    private readonly RequestPasswordResetHandler _requestPasswordResetHandler;
    private readonly ResetPasswordHandler _resetPasswordHandler;

    public AuthController(
        RegisterMemberHandler registerMemberHandler,
        LoginHandler loginHandler,
        RefreshTokenHandler refreshTokenHandler,
        LogoutHandler logoutHandler,
        RequestPasswordResetHandler requestPasswordResetHandler,
        ResetPasswordHandler resetPasswordHandler)
    {
        _registerMemberHandler = registerMemberHandler;
        _loginHandler = loginHandler;
        _refreshTokenHandler = refreshTokenHandler;
        _logoutHandler = logoutHandler;
        _requestPasswordResetHandler = requestPasswordResetHandler;
        _resetPasswordHandler = resetPasswordHandler;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterMemberRequest request, CancellationToken cancellationToken)
    {
        var result = await _registerMemberHandler.HandleAsync(request, cancellationToken);
        return result.ToActionResult(id => StatusCode(StatusCodes.Status201Created, new { id }));
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await _loginHandler.HandleAsync(request, cancellationToken);
        return result.ToActionResult(Ok);
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(RefreshTokenRequest request, CancellationToken cancellationToken)
    {
        var result = await _refreshTokenHandler.HandleAsync(request, cancellationToken);
        return result.ToActionResult(Ok);
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(LogoutRequest request, CancellationToken cancellationToken)
    {
        var result = await _logoutHandler.HandleAsync(User.GetMemberId(), request, cancellationToken);
        return result.ToActionResult();
    }

    [HttpPost("password-reset/request")]
    public async Task<IActionResult> RequestPasswordReset(RequestPasswordResetRequest request, CancellationToken cancellationToken)
    {
        // 刻意忽略 Handler 回傳的明文 Token，且無論 Email 是否存在都回傳相同訊息，避免帳號枚舉與 Token 外洩（見 design.md）。
        await _requestPasswordResetHandler.HandleAsync(request, cancellationToken);
        return Ok(new { message = "如果該 Email 已註冊，密碼重設說明已經產生。" });
    }

    [HttpPost("password-reset/confirm")]
    public async Task<IActionResult> ConfirmPasswordReset(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        var result = await _resetPasswordHandler.HandleAsync(request, cancellationToken);
        return result.ToActionResult();
    }
}
