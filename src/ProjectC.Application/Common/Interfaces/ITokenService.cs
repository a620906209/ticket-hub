using ProjectC.Domain.Members;

namespace ProjectC.Application.Common.Interfaces;

public interface ITokenService
{
    string GenerateAccessToken(Member member, Guid? organizerId = null);

    string GenerateOpaqueToken();

    string HashOpaqueToken(string token);
}
