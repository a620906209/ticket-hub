using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ProjectC.Application.Common;
using ProjectC.Infrastructure.Security;

namespace ProjectC.WebApi.Tests.TestSupport;

public static class SignedAccessTokenFactory
{
    /// <summary>比照 JwtTokenService 的 claim 組成自行簽發，只為了能指定過去的過期時間（不真實等待，避免測試變慢）。
    /// 呼叫端應把過期時間設在 ClockSkew（30 秒）之外。</summary>
    public static string CreateSignedAccessToken(
        IServiceProvider services, Guid memberId, string email, Guid? organizerId, DateTime expiresAtUtc)
    {
        var jwtOptions = services.GetRequiredService<IOptions<JwtOptions>>().Value;
        var signingCredentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, memberId.ToString()),
            new(JwtRegisteredClaimNames.Email, email),
            new(ClaimTypes.Role, "Member"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };
        if (organizerId is { } id)
        {
            claims.Add(new Claim(CustomClaimTypes.OrganizerId, id.ToString()));
        }

        var token = new JwtSecurityToken(
            issuer: jwtOptions.Issuer,
            audience: jwtOptions.Audience,
            claims: claims,
            notBefore: expiresAtUtc.AddMinutes(-30),
            expires: expiresAtUtc,
            signingCredentials: signingCredentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
