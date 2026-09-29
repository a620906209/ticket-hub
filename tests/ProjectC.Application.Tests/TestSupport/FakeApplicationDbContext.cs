using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Authentication;
using ProjectC.Domain.Members;
using ProjectC.Domain.Organizers;

namespace ProjectC.Application.Tests.TestSupport;

public sealed class FakeApplicationDbContext : IApplicationDbContext
{
    public List<Member> MemberData { get; } = new();
    public List<RefreshToken> RefreshTokenData { get; } = new();
    public List<PasswordResetToken> PasswordResetTokenData { get; } = new();
    public List<Organizer> OrganizerData { get; } = new();
    public List<OrganizerMember> OrganizerMemberData { get; } = new();

    public Exception? ExceptionToThrowOnNextSaveChanges { get; set; }
    public int SaveChangesCallCount { get; private set; }

    public DbSet<Member> Members { get; }
    public DbSet<RefreshToken> RefreshTokens { get; }
    public DbSet<PasswordResetToken> PasswordResetTokens { get; }
    public DbSet<Organizer> Organizers { get; }
    public DbSet<OrganizerMember> OrganizerMembers { get; }

    public FakeApplicationDbContext()
    {
        Members = BuildTrackedDbSet(MemberData);
        RefreshTokens = BuildTrackedDbSet(RefreshTokenData);
        PasswordResetTokens = BuildTrackedDbSet(PasswordResetTokenData);
        Organizers = BuildTrackedDbSet(OrganizerData);
        OrganizerMembers = BuildTrackedDbSet(OrganizerMemberData);
    }

    private static DbSet<T> BuildTrackedDbSet<T>(List<T> backingList) where T : class
    {
        var mock = backingList.BuildMockDbSet();
        mock.Setup(m => m.Add(It.IsAny<T>())).Callback<T>(entity => backingList.Add(entity));
        mock.Setup(m => m.Remove(It.IsAny<T>())).Callback<T>(entity => backingList.Remove(entity));
        return mock.Object;
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveChangesCallCount++;

        if (ExceptionToThrowOnNextSaveChanges is { } exception)
        {
            ExceptionToThrowOnNextSaveChanges = null;
            throw exception;
        }

        return Task.FromResult(0);
    }
}
