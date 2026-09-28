using Microsoft.AspNetCore.Authorization;

namespace ProjectC.WebApi.Common;

/// <summary>
/// fail-closed：只呼叫 <see cref="ClaimsPrincipalExtensions.TryGetOrganizerId"/> 判定是否通過（不得只用
/// <c>HasClaim</c> 檢查存在性），驗證失敗（含解析例外）一律不 Succeed，最終由 ASP.NET Core 回傳 403，
/// 不會變成非預期的 500（見 organizer-management spec.md ORG-POLICY-001～004）。
/// </summary>
public sealed class RequireOrganizerContextHandler : AuthorizationHandler<RequireOrganizerContextRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, RequireOrganizerContextRequirement requirement)
    {
        if (context.User.TryGetOrganizerId(out _))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
