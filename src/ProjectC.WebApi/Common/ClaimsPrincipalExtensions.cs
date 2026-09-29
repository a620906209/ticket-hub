using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ProjectC.Application.Common;

namespace ProjectC.WebApi.Common;

public static class ClaimsPrincipalExtensions
{
    public static Guid GetMemberId(this ClaimsPrincipal user)
    {
        var value = user.FindFirstValue(JwtRegisteredClaimNames.Sub);
        return Guid.Parse(value!);
    }

    /// <summary>
    /// 取出 <see cref="CustomClaimTypes.OrganizerId"/> claim 並驗證可解析為合法、非 <see cref="Guid.Empty"/>
    /// 的 <see cref="Guid"/>。claim 不存在、空字串、無法解析、或為 <see cref="Guid.Empty"/> 皆回傳 <c>false</c>，
    /// 不拋出例外（fail-closed，見 organizer-management spec.md「RequireOrganizerContext Authorization
    /// Policy MUST 驗證 claim 格式並 fail-closed」）。共用邏輯，任何需要讀取呼叫端目前 OrganizerId 的
    /// Controller／Policy Handler MUST 呼叫這個方法，不得各自另外呼叫 <see cref="Guid.Parse(string)"/>。
    /// </summary>
    public static bool TryGetOrganizerId(this ClaimsPrincipal user, out Guid organizerId)
    {
        var value = user.FindFirstValue(CustomClaimTypes.OrganizerId);
        if (!string.IsNullOrEmpty(value) && Guid.TryParse(value, out var parsed) && parsed != Guid.Empty)
        {
            organizerId = parsed;
            return true;
        }

        organizerId = Guid.Empty;
        return false;
    }
}
