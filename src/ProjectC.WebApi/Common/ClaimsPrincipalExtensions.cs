using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace ProjectC.WebApi.Common;

public static class ClaimsPrincipalExtensions
{
    public static Guid GetMemberId(this ClaimsPrincipal user)
    {
        var value = user.FindFirstValue(JwtRegisteredClaimNames.Sub);
        return Guid.Parse(value!);
    }
}
