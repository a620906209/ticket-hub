using Microsoft.EntityFrameworkCore;
using ProjectC.Domain.Authentication;
using ProjectC.Domain.Members;

namespace ProjectC.Application.Common.Interfaces;

public interface IApplicationDbContext
{
    DbSet<Member> Members { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<PasswordResetToken> PasswordResetTokens { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
