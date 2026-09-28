using Microsoft.EntityFrameworkCore;
using ProjectC.Domain.Authentication;
using ProjectC.Domain.Members;
using ProjectC.Domain.Organizers;

namespace ProjectC.Application.Common.Interfaces;

public interface IApplicationDbContext
{
    DbSet<Member> Members { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<PasswordResetToken> PasswordResetTokens { get; }
    DbSet<Organizer> Organizers { get; }
    DbSet<OrganizerMember> OrganizerMembers { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
